# Configuration Store for AWS Parameter Store

Enables management of individual connection settings with encryption support for securely storing keys and secrets in the [AWS Systems Manager Parameter Store](https://docs.aws.amazon.com/systems-manager/latest/userguide/systems-manager-parameter-store.html).

## Configure AWS SDK

Ensure that the AWS SDK has the necessary [configured AWS credentials](https://docs.aws.amazon.com/sdk-for-net/latest/developer-guide/net-dg-config-creds.html).

## Configure API Connections

Use the AWS console's UI (or other preferred mechanism) to manage API connection details under `/ed-fi/apiPublisher/connections/{connectionName}/`, using the logical names defined in [API Connection Management](../API-Connection-Management.md).

### URL, key, and secret (consolidated `credentials` parameter)

To reduce the number of standard-tier parameters, store the API base URL, client key, and client secret in a **single** SecureString parameter named `credentials` whose value is JSON, for example:

```json
{
  "url": "https://your-district.example.com/ods",
  "key": "yourClientId",
  "secret": "yourClientSecret"
}
```

Property names `url`, `key`, and `secret` are matched case-insensitively. Each property is optional inside the JSON; a non-empty string for a property overwrites the value read from legacy flat parameters (see below). You must end up with all three values defined (via JSON, legacy parameters, or a combination).

If `credentials` is present and its value is non-empty but **not** valid JSON, the API Publisher fails with an error referencing the parameter path (invalid JSON is never ignored). A whitespace-only `credentials` value is ignored so legacy-only connections keep working.

### Legacy flat parameters (backward compatible)

The publisher still supports separate parameters:

- `/ed-fi/apiPublisher/connections/{connectionName}/url` (String)
- `/ed-fi/apiPublisher/connections/{connectionName}/key` (SecureString)
- `/ed-fi/apiPublisher/connections/{connectionName}/secret` (SecureString)

During migration you may define both `credentials` and one or more of these; non-empty JSON properties take precedence over the flat parameters for those fields.

### Other connection options

Optional settings (for example `include`, `ignoreIsolation`) remain separate parameters under the same connection path. When creating SecureString parameters for secrets, use the _SecureString_ type.

![AWS](../../images/Aws-Parameter-Store-configuration-store-example.png)

## Configure API Publisher

To use the AWS Parameter Store for you connection management, change the `provider` setting in the _configurationStoreSettings.json_ file to `awsParameterStore` and supply the appropriate AWS SDK initialization parameters, as shown below (see the [AWS SDK for .NET Core documentation](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/net-dg-config-netcore.html) for more information):

```json
{
  "configurationStore": {
    "provider": "awsParameterStore",
    "awsParameterStore": {
      "Profile": "default",
      "Region": "us-east-1"
    }
  }
}
```
