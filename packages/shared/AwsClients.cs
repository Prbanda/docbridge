using Amazon.S3;
using Amazon.SQS;

namespace DocBridge.Shared;

/// <summary>
/// AWS clients configured from the environment. AWS_ENDPOINT_URL points at LocalStack locally;
/// in AWS it's unset and the SDK uses the task/function role and regional endpoints.
/// </summary>
public static class AwsClients
{
    public static AmazonS3Client S3(string? endpointOverride = null)
    {
        var config = new AmazonS3Config { RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(Region) };
        if ((endpointOverride ?? Env.Optional("AWS_ENDPOINT_URL")) is { } endpoint)
        {
            config.ServiceURL = endpoint;
            config.ForcePathStyle = true;
            config.AuthenticationRegion = Region;
        }
        return new AmazonS3Client(config);
    }

    public static AmazonSQSClient Sqs()
    {
        var config = new AmazonSQSConfig { RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(Region) };
        if (Env.Optional("AWS_ENDPOINT_URL") is { } endpoint)
        {
            config.ServiceURL = endpoint;
            config.AuthenticationRegion = Region;
        }
        return new AmazonSQSClient(config);
    }

    public static string Region => Env.Get("AWS_REGION", "us-east-1");
}
