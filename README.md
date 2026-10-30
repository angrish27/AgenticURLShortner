# Agentic URL Shortener

A .NET Web API that turns valid long URLs into short links and redirects short links to their original destinations. The service persists URL mappings in a JSON file and exposes a health-check endpoint.

## What this repository does

- `POST /api/url/encode` validates a URL and returns a short URL. A newly stored URL returns `201 Created`; submitting the same URL again returns `200 OK` with the existing short URL.
- `GET /{code}` redirects to the original URL with `302 Found`. Unknown or invalid codes return `404 Not Found`.
- New short codes are URL-safe Base64 encodings of the request-received Unix epoch timestamp in nanoseconds. The store advances timestamps monotonically to avoid collisions when requests arrive within the same system-clock tick. Legacy Base62 links remain supported.
- Mappings are stored in `App_Data/url-mappings.json` by default.
- Each stored mapping has a sequential `seqNumber` used as an internal record ID; it is separate from the short code and is not included in the short URL.
- The API also provides `GET /health`.

The API accepts absolute HTTP or HTTPS URLs up to 2,048 characters. It rejects malformed URLs and URLs that point back to the shortener service. The encode endpoint has an 8 KB request-body limit and a default rate limit of 30 requests per minute per process.

## Tech used

- C# and .NET 10
- ASP.NET Core Web API
- MSTest for unit and HTTP integration tests
- JSON-file persistence behind a storage interface
- Pulumi with the Azure Native provider for the infrastructure project

## Agentic AI development

This project uses AI coding agents as active participants in software development, not just for one-off code suggestions. Repository guidance gives agents the architecture and conventions they need to plan a task, implement changes across the appropriate layers, run tests, iterate on failures, and report their results. A human reviews and approves changes before they are merged.

The workflow is defined in the repository root [`AGENTS.md`](AGENTS.md), with detailed architecture and coding guidance in [`AgentUrlShortner/docs/agent-instructions.md`](AgentUrlShortner/docs/agent-instructions.md). See the [agent-driven change example](AgentUrlShortner/docs/agentic-development-example.md) for a sample task and its test report. The API itself remains a conventional URL shortener; it does not require AI to run.

## IDesign approach

The backend follows an IDesign Method-style, volatility-based layered architecture. Requests flow through:

```text
Client (Web API) → Manager → Engine → Accessor
                         └──── Utilities and Common are shared across layers
```

- **Clients** expose HTTP endpoints and handle HTTP-specific concerns.
- **Managers** orchestrate use cases.
- **Engines** contain business rules and validation.
- **Accessors** handle persistence and external resources.
- **Utilities** provide stateless helpers; **Common** contains shared infrastructure such as factories, context, constants, and exceptions.
- **Contracts** define client-facing DTOs and internal logic models; **Tests** cover the layers and API.

Dependencies flow down the layers: clients call managers, and lower layers do not call back into higher layers. See [`architecture.md`](AgentUrlShortner/docs/architecture.md) for the detailed dependency rules.

## How to set up

### Prerequisites

- .NET 10 SDK
- Git

### Run the API locally

From the repository root:

```bash
dotnet dev-certs https --trust
dotnet run --project "AgentUrlShortner/Backend/1. Clients/CS.AgenticUrlShortner.WebApiClient/CS.AgenticUrlShortner.WebApiClient.csproj"
```

The development launch profile listens at:

- HTTPS: `https://localhost:7181`
- HTTP: `http://localhost:5181` (redirects to HTTPS)

Check that the API is healthy at [`https://localhost:7181/health`](https://localhost:7181/health). The default data file is created when the first URL is encoded, under the API content root. Ensure that directory is writable. The default JSON-file store and its in-process locking are intended for a single running instance; see [`decisions.md`](AgentUrlShortner/docs/decisions.md).

### Build and test

From the repository root:

```bash
dotnet build AgentUrlShortner/Backend/CS.AgenticUrlShortner.sln
dotnet test AgentUrlShortner/Backend/CS.AgenticUrlShortner.sln
```

The test suite uses MSTest and includes API integration tests. See [`testing.md`](AgentUrlShortner/docs/testing.md) for test projects and conventions.

## Sample request and expected response

Send a `POST` request to:

```text
https://localhost:7181/api/url/encode
```

Set `Content-Type: application/json` and use this JSON body:

```json
{
  "longUrl": "https://example.com/my-page"
}
```

For example, with `curl`:

```bash
curl -i -X POST "https://localhost:7181/api/url/encode" \
  -H "Content-Type: application/json" \
  -d '{"longUrl":"https://example.com/my-page"}'
```

For a new URL, expect `201 Created`, a `Location` header containing the short URL, and a response body like:

```json
{
  "shortUrl": "https://localhost:7181/<generated-epoch-token>"
}
```

The generated token is an 11-character, URL-safe Base64 value and varies with request time and existing mappings. Repeating the same request returns `200 OK` and the existing short URL. Opening that URL redirects to the original URL. Existing mappings stored with the older Base62 format continue to work.

For Postman, choose **POST**, enter the endpoint URL, then select **Body → raw → JSON** and paste the request body above. If Postman reports a certificate error with the local HTTPS endpoint, trust the .NET development certificate using the setup command above.
