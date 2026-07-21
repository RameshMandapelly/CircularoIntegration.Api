using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CircularoIntegration.Api.Config;
using CircularoIntegration.Api.Data;

namespace CircularoIntegration.Api.Services;

public interface IKfsNotificationService
{
    Task HandleStageChangeAsync(string leadId, string newStageName, CancellationToken ct = default);
}