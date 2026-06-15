namespace ShippingManagement.Web.Models
{
    public class User
    {
        public int UserID { get; set; }
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Role { get; set; } = "User";
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
    }

    public class Company
    {
        public int CompanyID { get; set; }
        public string CompanyName { get; set; } = "";
        public string? Address { get; set; }
        public string? Country { get; set; }
        public string? GeneralEmail { get; set; }
        public string? Website { get; set; }
        public string? Telephone { get; set; }
        public string Status { get; set; } = "Non-Regular";   // Regular | Non-Regular
        public int FleetCount { get; set; }
    }

    public class VesselType
    {
        public int TypeID { get; set; }
        public string TypeName { get; set; } = "";
    }

    public class Vessel
    {
        public string IMO_Number { get; set; } = "";
        public string VesselName { get; set; } = "";
        public int? VesselTypeID { get; set; }
        public string? VesselType { get; set; }       // joined TypeName
        public string? CallSign { get; set; }
        public int? CompanyID { get; set; }
        public string? Port { get; set; }
        public string? ETA { get; set; }
        public string? Country { get; set; }
        public string? Address { get; set; }
        public string? PhoneNo { get; set; }
        public string? Terms { get; set; }
        public string? ConfirmEmail { get; set; }
        public string? GenerateEmail { get; set; }
        public string? DeckEngEmail { get; set; }
        public string? CateringEmail { get; set; }
        public string? PurchaseEmail { get; set; }
        public string? GeneralEmail { get; set; }
        public string Status { get; set; } = "Active";
        public Company? Company { get; set; }
        public string? CompanyName { get; set; }      // flat join
        public string? CustomerStatus { get; set; }   // company Regular/Non-Regular (for highlighting)
    }

    public class CountryItem
    {
        public int CountryID { get; set; }
        public string CountryName { get; set; } = "";
        public bool IsAsia { get; set; }
    }

    public class Port
    {
        public int PortID { get; set; }
        public string PortName { get; set; } = "";
        public int CountryID { get; set; }
        public string? CountryName { get; set; }
        public string? Notes { get; set; }
        public int MaxPages { get; set; } = 50;
        public int SourceCount { get; set; }
        // assignment info (joined)
        public int? AssignedUserID { get; set; }
        public string? AssignedUserName { get; set; }
    }

    public class PortSource
    {
        public int SourceID { get; set; }
        public int PortID { get; set; }
        public string SourceName { get; set; } = "";
        public string Url { get; set; } = "";
        public string? PageParamPattern { get; set; }
        public int StartPage { get; set; } = 1;
        public int EndPage { get; set; } = 50;
        public bool IsActive { get; set; } = true;
    }

    public class ScrapedRecord
    {
        public int ScrapeID { get; set; }
        public bool IsSaved { get; set; }      // set when the row was saved to ArrivalLog history
        public string VesselName { get; set; } = "";
        public string? IMO_Number { get; set; }
        public int? PortID { get; set; }
        public string PortName { get; set; } = "";
        public string Country { get; set; } = "";
        public string? ArrivalDate { get; set; }
        public string? DepartureTime { get; set; }
        public string? Origin { get; set; }
        public string? VesselStatus { get; set; }
        public string? DataSource { get; set; }
        public string? Deadweight { get; set; }
        public string? GrossTonnage { get; set; }
        public string? VesselBuilt { get; set; }
        public string? VesselType { get; set; }
        public string? VesselSize { get; set; }
        public bool IsMatched { get; set; }
        public bool IsUseless { get; set; }
        public int? AssignedUserID { get; set; }
        public string? AssignedUserName { get; set; }
        public DateTime ImportDate { get; set; }
        public string? CustomerStatus { get; set; }   // for regular-customer highlighting
    }

    public class ArrivalLog
    {
        public int LogID { get; set; }
        public string? IMO_Number { get; set; }
        public string PortName { get; set; } = "";
        public string Country { get; set; } = "";
        public DateTime ArrivalDate { get; set; }
        public bool IsTagged { get; set; }
        public int? EnteredBy { get; set; }
        // joined (vw_ArrivalDetail)
        public string? VesselName { get; set; }
        public string? VesselType { get; set; }
        public string? CallSign { get; set; }
        public string? Status { get; set; }
        public string? Terms { get; set; }
        public string? ConfirmEmail { get; set; }
        public string? PurchaseEmail { get; set; }
        public string? CateringEmail { get; set; }
        public string? GenerateEmail { get; set; }
        public string? DeckEngEmail { get; set; }
        public string? GeneralEmail { get; set; }
        public string? CompanyName { get; set; }
        public string? CompanyAddress { get; set; }
        public string? CompanyCountry { get; set; }
        public string? CompanyEmail { get; set; }
        public string? Telephone { get; set; }
        public string? Website { get; set; }
        public string? CustomerStatus { get; set; }
    }

    /// <summary>Active data-source row (source + port + country) passed to the Python scraper.</summary>
    public class ScrapeSourceInfo
    {
        public int SourceID { get; set; }
        public string SourceName { get; set; } = "";
        public string Url { get; set; } = "";
        public string? PageParamPattern { get; set; }
        public int StartPage { get; set; } = 1;
        public int EndPage { get; set; } = 50;
        public int PortID { get; set; }
        public string PortName { get; set; } = "";
        public int MaxPages { get; set; } = 50;
        public string CountryName { get; set; } = "";
    }

    /// <summary>Auto Emails — one sent/logged message record.</summary>
    public class EmailLog
    {
        public int EmailID { get; set; }
        public string Category { get; set; } = "General";
        public string ToAddress { get; set; } = "";
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? IMO_Number { get; set; }
        public string? VesselName { get; set; }
        public string? CompanyName { get; set; }
        public string Status { get; set; } = "Sent";   // Sent | Failed | Logged
        public string? ErrorText { get; set; }
        public int? SentBy { get; set; }
        public DateTime SentAt { get; set; }
    }
}
