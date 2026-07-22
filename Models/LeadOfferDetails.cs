namespace CircularoIntegration.Api.Models;

public class LeadOfferDetails
{
    public string? CustomerName { get; set; }
    public string? AuthorizedSignatoryEmail { get; set; }
    public string? FundedFacilityLimit { get; set; }
    public string? RequestedFinanceAmount { get; set; }
    public string? Purpose { get; set; }
    public string? Tenor { get; set; }
    public string? ProfitRate { get; set; }
    public string? Frequency { get; set; }
    public string? PaymentTerms { get; set; }

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(CustomerName) &&
        !string.IsNullOrWhiteSpace(AuthorizedSignatoryEmail) &&
        !string.IsNullOrWhiteSpace(FundedFacilityLimit) &&
        !string.IsNullOrWhiteSpace(RequestedFinanceAmount) &&
        !string.IsNullOrWhiteSpace(Purpose) &&
        !string.IsNullOrWhiteSpace(Tenor) &&
        !string.IsNullOrWhiteSpace(ProfitRate) &&
        !string.IsNullOrWhiteSpace(Frequency) &&
        !string.IsNullOrWhiteSpace(PaymentTerms);
}