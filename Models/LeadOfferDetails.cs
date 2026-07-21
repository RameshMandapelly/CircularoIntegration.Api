namespace CircularoIntegration.Api.Models;

public class LeadOfferDetails
{
          public string? AuthorizedSignatoryEmail { get; set; }
          public string? FirstName { get; set; }
          public string? FundedFacilityLimit { get; set; }
          public string? RequestedFinanceAmount { get; set; }
          public string? Purpose { get; set; }
          public string? Tenure { get; set; }
          public string? ProfitRate { get; set; }

          public bool IsComplete =>
              !string.IsNullOrWhiteSpace(AuthorizedSignatoryEmail) &&
              !string.IsNullOrWhiteSpace(FundedFacilityLimit) &&
              !string.IsNullOrWhiteSpace(RequestedFinanceAmount) &&
              !string.IsNullOrWhiteSpace(Purpose) &&
              !string.IsNullOrWhiteSpace(Tenure) &&
              !string.IsNullOrWhiteSpace(ProfitRate);
}