using Autodesk.Forge;
using Autodesk.Forge.Client;
using Autodesk.Forge.Model;

public record Token(string access_token, DateTime expires_at);

public class ForgeService
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _bucket;
    private Token _internalTokenCache;
    private Token _publicTokenCache;

    public ForgeService(string clientId, string clientSecret, string bucket = null)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _bucket = string.IsNullOrEmpty(bucket) ? string.Format("{0}-basic-app", _clientId.ToLower()) : bucket;
    }

    public async Task<IEnumerable<dynamic>> GetObjects()
    {
        const int PageSize = 64;
        await EnsureBucketExists(_bucket);
        var token = await GetInternalToken();
        var api = new ObjectsApi();
        api.Configuration.AccessToken = token.access_token;
        var objects = new List<dynamic>();
        dynamic response = await api.GetObjectsAsync(_bucket, PageSize);
        foreach (KeyValuePair<string, dynamic> obj in new DynamicDictionaryItems(response.items))
        {
            objects.Add(new { name = obj.Value.objectKey, urn = Base64Encode(obj.Value.objectId) });
        }
        while ((response as DynamicDictionary).Dictionary.ContainsKey("next")) // This feels hacky... is there a better way?
        {
            var queryParams = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(response.next).Query);
            response = await api.GetObjectsAsync(_bucket, PageSize, null, queryParams["startAt"]);
            foreach (KeyValuePair<string, dynamic> obj in new DynamicDictionaryItems(response.items))
            {
                objects.Add(new { name = obj.Value.objectKey, urn = Base64Encode(obj.Value.objectId) });
            }
        }
        return objects;
    }

    public async Task<dynamic> UploadModel(string objectName, Stream content, long contentLength)
    {
        await EnsureBucketExists(_bucket);
        var token = await GetInternalToken();
        var api = new ObjectsApi();
        api.Configuration.AccessToken = token.access_token;
        dynamic obj = await api.UploadObjectAsync(_bucket, objectName, (int)contentLength, content);
        return obj;
    }

    public async Task<dynamic> TranslateModel(string objectId, string rootFilename)
    {
        var token = await GetInternalToken();
        var api = new DerivativesApi();
        api.Configuration.AccessToken = token.access_token;
        var formats = new List<JobPayloadItem> {
                new JobPayloadItem (JobPayloadItem.TypeEnum.Svf, new List<JobPayloadItem.ViewsEnum> { JobPayloadItem.ViewsEnum._2d, JobPayloadItem.ViewsEnum._2d })
            };
        var payload = new JobPayload(
            new JobPayloadInput(Base64Encode(objectId)),
            new JobPayloadOutput(formats)
        );
        if (!string.IsNullOrEmpty(rootFilename))
        {
            payload.Input.RootFilename = rootFilename;
            payload.Input.CompressedUrn = true;
        }
        dynamic job = await api.TranslateAsync(payload);
        return job;
    }

    public async Task EnsureBucketExists(string bucketKey)
    {
        var token = await GetInternalToken();
        var api = new BucketsApi();
        api.Configuration.AccessToken = token.access_token;
        try
        {
            await api.GetBucketDetailsAsync(bucketKey);
        }
        catch (ApiException e)
        {
            if (e.ErrorCode == 404)
            {
                await api.CreateBucketAsync(new PostBucketsPayload(bucketKey, null, PostBucketsPayload.PolicyKeyEnum.Temporary));
            }
            else
            {
                throw e;
            }
        }
    }

    public async Task<Token> GetPublicToken()
    {
        if (_publicTokenCache == null || _publicTokenCache.expires_at < DateTime.UtcNow)
        {
            _publicTokenCache = await GetToken(new Scope[] { Scope.ViewablesRead });
        }
        return _publicTokenCache;
    }

    public async Task<Token> GetInternalToken()
    {
        if (_internalTokenCache == null || _internalTokenCache.expires_at < DateTime.UtcNow)
        {
            _internalTokenCache = await GetToken(new Scope[] { Scope.BucketCreate, Scope.BucketRead, Scope.DataRead, Scope.DataWrite, Scope.DataCreate });
        }
        return _internalTokenCache;
    }

    private async Task<Token> GetToken(Scope[] scopes)
    {
        dynamic auth = await new TwoLeggedApi().AuthenticateAsync(_clientId, _clientSecret, "client_credentials", scopes);
        return new Token(auth.access_token, DateTime.UtcNow.AddSeconds(auth.expires_in));
    }
    private static string Base64Encode(string plainText)
    {
        var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
        return System.Convert.ToBase64String(plainTextBytes).TrimEnd('=');
    }
}
