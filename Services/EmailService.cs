using System.Net;
using System.Net.Mail;
using ShippingManagement.Web.Data;
using ShippingManagement.Web.Models;

namespace ShippingManagement.Web.Services
{
    /// <summary>
    /// Auto Emails module — sends category-based emails (Confirm / Purchase / Catering /
    /// Generate / Deck-Eng / General) to the addresses stored on each vessel, using the
    /// SMTP settings in appsettings.json ("Email" section).
    ///
    /// When Email:Enabled is false (the default), nothing leaves the building: every message
    /// is recorded in EmailLog with status "Logged" so the workflow is fully demonstrable
    /// without real SMTP credentials. Flip Email:Enabled to true and fill in the SMTP host /
    /// credentials to send for real.
    /// </summary>
    public class EmailService
    {
        private readonly IConfiguration _cfg;
        private readonly ShippingRepository _repo;
        private readonly ILogger<EmailService> _log;
        private readonly List<EmailProfile> _profiles;

        public EmailService(IConfiguration cfg, ShippingRepository repo, ILogger<EmailService> log)
        {
            _cfg = cfg; _repo = repo; _log = log;
            _profiles = LoadProfiles(cfg);
        }

        public bool IsEnabled => _cfg.GetValue("Email:Enabled", false);

        /// <summary>The configured "send from" accounts (Google / Hotmail / …) shown in the UI.</summary>
        public IReadOnlyList<EmailProfile> Profiles => _profiles;

        /// <summary>Resolves a profile by name (case-insensitive); falls back to the first/default.</summary>
        public EmailProfile ResolveProfile(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                var match = _profiles.FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }
            var defName = _cfg["Email:DefaultProfile"];
            return _profiles.FirstOrDefault(p =>
                       string.Equals(p.Name, defName, StringComparison.OrdinalIgnoreCase))
                   ?? _profiles[0];
        }

        /// <summary>
        /// Reads the "Email:Profiles" section if present (multiple named SMTP accounts).
        /// If that section is missing it falls back to the legacy flat "Email:*" keys so
        /// existing configurations keep working — exposed as a single "Default" profile.
        /// </summary>
        private static List<EmailProfile> LoadProfiles(IConfiguration cfg)
        {
            var list = new List<EmailProfile>();
            var section = cfg.GetSection("Email:Profiles");
            if (section.Exists())
            {
                foreach (var child in section.GetChildren())
                {
                    list.Add(new EmailProfile
                    {
                        Name        = child["Name"] ?? child.Key,
                        Host        = child["Host"] ?? "",
                        Port        = int.TryParse(child["Port"], out var p) ? p : 587,
                        EnableSsl   = !bool.TryParse(child["EnableSsl"], out var ssl) || ssl,
                        Username    = child["Username"],
                        Password    = child["Password"],
                        FromAddress = child["FromAddress"] ?? "no-reply@localhost",
                        FromName    = child["FromName"] ?? "Shipping Management System"
                    });
                }
            }

            if (list.Count == 0)
            {
                // Legacy single-account configuration.
                list.Add(new EmailProfile
                {
                    Name        = "Default",
                    Host        = cfg["Email:Host"] ?? "",
                    Port        = cfg.GetValue("Email:Port", 587),
                    EnableSsl   = cfg.GetValue("Email:EnableSsl", true),
                    Username    = cfg["Email:Username"],
                    Password    = cfg["Email:Password"],
                    FromAddress = cfg["Email:FromAddress"] ?? "no-reply@localhost",
                    FromName    = cfg["Email:FromName"] ?? "Shipping Management System"
                });
            }
            return list;
        }

        /// <summary>Sends (or logs) a single email and records the outcome in EmailLog.</summary>
        public EmailResult Send(EmailMessage msg, int sentBy, string? profileName = null)
        {
            var profile = ResolveProfile(profileName);
            var log = new EmailLog
            {
                Category    = msg.Category,
                ToAddress   = msg.ToAddress,
                Subject     = msg.Subject,
                Body        = msg.Body,
                IMO_Number  = msg.IMO_Number,
                VesselName  = msg.VesselName,
                CompanyName = msg.CompanyName,
                SentBy      = sentBy,
                SentVia     = profile.Name
            };

            if (string.IsNullOrWhiteSpace(msg.ToAddress))
            {
                log.Status = "Failed";
                log.ErrorText = "No recipient address on record for this category.";
                _repo.InsertEmailLog(log);
                return new EmailResult(false, log.ErrorText);
            }

            if (!IsEnabled)
            {
                log.Status = "Logged";
                _repo.InsertEmailLog(log);
                return new EmailResult(true, "Logged (SMTP disabled).");
            }

            try
            {
                using var client = BuildClient(profile);
                using var mail = new MailMessage
                {
                    From = new MailAddress(profile.FromAddress, profile.FromName),
                    Subject = msg.Subject ?? "",
                    Body = msg.Body ?? "",
                    IsBodyHtml = msg.IsHtml
                };
                foreach (var addr in SplitAddresses(msg.ToAddress))
                    mail.To.Add(addr);

                client.Send(mail);
                log.Status = "Sent";
                _repo.InsertEmailLog(log);
                return new EmailResult(true, "Sent.");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to send {Category} email to {To} via {Profile}",
                    msg.Category, msg.ToAddress, profile.Name);
                log.Status = "Failed";
                log.ErrorText = ex.Message.Length > 480 ? ex.Message[..480] : ex.Message;
                _repo.InsertEmailLog(log);
                return new EmailResult(false, ex.Message);
            }
        }

        /// <summary>Sends a batch and returns (sent, failed, logged) counts.</summary>
        public (int sent, int failed, int logged) SendBatch(IEnumerable<EmailMessage> messages, int sentBy, string? profileName = null)
        {
            int sent = 0, failed = 0, logged = 0;
            foreach (var m in messages)
            {
                var r = Send(m, sentBy, profileName);
                if (!r.Ok) failed++;
                else if (!IsEnabled) logged++;
                else sent++;
            }
            return (sent, failed, logged);
        }

        private SmtpClient BuildClient(EmailProfile profile) => new(profile.Host, profile.Port)
        {
            EnableSsl = profile.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = string.IsNullOrWhiteSpace(profile.Username)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(profile.Username, profile.Password)
        };

        /// <summary>A vessel email field can hold several addresses (comma/semicolon/newline separated).</summary>
        public static IEnumerable<string> SplitAddresses(string? raw) =>
            (raw ?? "")
                .Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Contains('@'))
                .Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>The email categories that map to the per-vessel address fields.</summary>
        public static readonly string[] Categories =
            { "Confirm", "Purchase", "Catering", "Generate", "DeckEng", "General" };

        /// <summary>Resolves which stored address a category should use for a given arrival row.</summary>
        public static string? CategoryAddress(ArrivalLog a, string category) => category switch
        {
            "Confirm"  => a.ConfirmEmail,
            "Purchase" => a.PurchaseEmail,
            "Catering" => a.CateringEmail,
            "Generate" => a.GenerateEmail,
            "DeckEng"  => a.DeckEngEmail,
            _          => a.GeneralEmail ?? a.CompanyEmail
        };
    }

    public record EmailResult(bool Ok, string Message);

    /// <summary>A single "send from" account (e.g. Google or Hotmail) the user can pick in the UI.</summary>
    public class EmailProfile
    {
        public string Name { get; set; } = "Default";
        public string Host { get; set; } = "";
        public int Port { get; set; } = 587;
        public bool EnableSsl { get; set; } = true;
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string FromAddress { get; set; } = "no-reply@localhost";
        public string? FromName { get; set; } = "Shipping Management System";
    }

    public class EmailMessage
    {
        public string Category { get; set; } = "General";
        public string? ToAddress { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public bool IsHtml { get; set; }
        public string? IMO_Number { get; set; }
        public string? VesselName { get; set; }
        public string? CompanyName { get; set; }
    }
}
