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

        public EmailService(IConfiguration cfg, ShippingRepository repo, ILogger<EmailService> log)
        {
            _cfg = cfg; _repo = repo; _log = log;
        }

        public bool IsEnabled => _cfg.GetValue("Email:Enabled", false);

        /// <summary>Sends (or logs) a single email and records the outcome in EmailLog.</summary>
        public EmailResult Send(EmailMessage msg, int sentBy)
        {
            var log = new EmailLog
            {
                Category    = msg.Category,
                ToAddress   = msg.ToAddress,
                Subject     = msg.Subject,
                Body        = msg.Body,
                IMO_Number  = msg.IMO_Number,
                VesselName  = msg.VesselName,
                CompanyName = msg.CompanyName,
                SentBy      = sentBy
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
                using var client = BuildClient();
                using var mail = new MailMessage
                {
                    From = new MailAddress(
                        _cfg["Email:FromAddress"] ?? "no-reply@localhost",
                        _cfg["Email:FromName"] ?? "Shipping Management System"),
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
                _log.LogError(ex, "Failed to send {Category} email to {To}", msg.Category, msg.ToAddress);
                log.Status = "Failed";
                log.ErrorText = ex.Message.Length > 480 ? ex.Message[..480] : ex.Message;
                _repo.InsertEmailLog(log);
                return new EmailResult(false, ex.Message);
            }
        }

        /// <summary>Sends a batch and returns (sent, failed) counts.</summary>
        public (int sent, int failed, int logged) SendBatch(IEnumerable<EmailMessage> messages, int sentBy)
        {
            int sent = 0, failed = 0, logged = 0;
            foreach (var m in messages)
            {
                var r = Send(m, sentBy);
                if (!r.Ok) failed++;
                else if (!IsEnabled) logged++;
                else sent++;
            }
            return (sent, failed, logged);
        }

        private SmtpClient BuildClient() => new(_cfg["Email:Host"], _cfg.GetValue("Email:Port", 587))
        {
            EnableSsl = _cfg.GetValue("Email:EnableSsl", true),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = string.IsNullOrWhiteSpace(_cfg["Email:Username"])
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(_cfg["Email:Username"], _cfg["Email:Password"])
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
