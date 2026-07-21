namespace CircularoIntegration.Api.Models;

/// <summary>Carries the downstream system's real HTTP status and raw response body.</summary>
public class DownstreamException : Exception
{
          public int StatusCode { get; }
          public string ResponseBody { get; }

          public DownstreamException(int statusCode, string responseBody)
              : base(responseBody)
          {
                    StatusCode = statusCode;
                    ResponseBody = responseBody;
          }
}