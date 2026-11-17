using System.Text.RegularExpressions;
using Trainer.TestSupport;

namespace Trainer.Deploy.Tests;

public sealed class NginxConfigTests
{
    private static readonly string NginxRoot = Path.Combine(ContentWorkspace.RepositoryRoot(), "deploy", "nginx");

    private static readonly string[] TemplateNames =
    [
        "nginx.conf.template",
        "dev-interview-trainer.conf.template",
        "dev-interview-trainer.http.conf.template",
    ];

    // An allowlist: a new variable in log_format must be a deliberate, reviewed decision (no address, no query string).
    private static readonly string[] AllowedLogVariables =
        ["$time_local", "$request_method", "$uri", "$server_protocol", "$status", "$body_bytes_sent", "$request_time"];

    // Every client-IP header Umami 3.5.0 reads (verified in the pinned image), besides the X-Forwarded-For nginx sets.
    private static readonly string[] BlankedClientIpHeaders =
    [
        "True-Client-IP", "CF-Connecting-IP", "Fastly-Client-IP", "X-NF-Client-Connection-IP", "DO-Connecting-IP", "X-Real-IP",
        "X-AppEngine-User-IP", "Forwarded", "X-Client-IP", "X-Cluster-Client-IP", "X-Forwarded", "X-Umami-Client-IP",
    ];

    private static readonly string[] AllowedPlaceholders = ["DOMAIN", "APP_DIR"];

    private static string Read(string name) => File.ReadAllText(Path.Combine(NginxRoot, name));

    private static string Site() => Read("dev-interview-trainer.conf.template");

    // The port 80 server has its own `location /`, so proxy assertions look only at the TLS server for the site.
    private static string TlsServer()
    {
        string site = Site();
        return site[site.LastIndexOf("listen 443 ssl http2;", StringComparison.Ordinal)..];
    }

    // The directive text of a location block, from its header to the matching closing brace.
    private static string Location(string config, string header)
    {
        int start = config.IndexOf("location " + header + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"location {header} is missing.");
        int depth = 0;
        for (int i = config.IndexOf('{', start); i < config.Length; i++)
        {
            if (config[i] == '{')
            {
                depth++;
            }
            else if (config[i] == '}' && --depth == 0)
            {
                return config[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"location {header} is not closed.");
    }

    [Fact]
    public void ProxiedLocation_HasGzipOff()
    {
        Assert.Contains("gzip off;", Location(TlsServer(), "/"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("= /stats/api/send")]
    public void ForwardedHeaders_OverwriteClientValues(string header)
    {
        string block = Location(TlsServer(), header);

        Assert.Contains("proxy_set_header X-Forwarded-For $remote_addr;", block);
        Assert.DoesNotMatch(@"proxy_set_header\s+X-Forwarded-For\s+\$proxy_add_x_forwarded_for", Site());
    }

    [Fact]
    public void AppLocation_SetsHostAndScheme()
    {
        string block = Location(TlsServer(), "/");

        Assert.Contains("proxy_set_header Host $host;", block);
        Assert.Contains("proxy_set_header X-Forwarded-Proto $scheme;", block);
        Assert.Contains("proxy_pass http://127.0.0.1:8080;", block);
    }

    [Fact]
    public void AccessLog_NeverLogsQueryStringsOrAddresses()
    {
        Match format = Regex.Match(Site(), @"log_format\s+dit\s+'([^']*)';");
        Assert.True(format.Success, "log_format dit is missing.");

        string[] used = [.. Regex.Matches(format.Groups[1].Value, @"\$[a-z_]+").Select(m => m.Value)];
        Assert.Equal(AllowedLogVariables.Order(StringComparer.Ordinal), used.Distinct().Order(StringComparer.Ordinal));
        Assert.Contains("access_log /var/log/nginx/dev-interview-trainer.access.log dit;", Site());
    }

    [Fact]
    public void HttpErrorAndAccessLogsOffByDefault()
    {
        string main = Read("nginx.conf.template");
        string httpBlock = main[main.IndexOf("http {", StringComparison.Ordinal)..];

        Assert.Matches(@"(?m)^\s*access_log off;", httpBlock);
        Assert.Matches(@"(?m)^\s*error_log /dev/null;", httpBlock);

        foreach (string name in TemplateNames.Skip(1))
        {
            Assert.DoesNotContain("error_log", Read(name));
        }

        Assert.Equal(1, Regex.Count(Site(), @"(?m)^\s*access_log "));
        Assert.DoesNotContain("access_log", Read("dev-interview-trainer.http.conf.template"));
    }

    [Fact]
    public void NoLimitReqLogLevel()
    {
        foreach (string name in TemplateNames)
        {
            Assert.DoesNotContain("limit_req_log_level", Read(name));
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("= /stats/api/send")]
    public void MaintenanceFile_Returns503(string header)
    {
        string block = Location(TlsServer(), header);

        Assert.Matches(@"if \(-f \$\{APP_DIR\}/flags/maintenance\) \{\s*return 503;\s*\}", block);
    }

    [Fact]
    public void Http2_UsesListenParameterFor124()
    {
        foreach (string name in TemplateNames)
        {
            Assert.DoesNotMatch(@"(?m)^\s*http2\s+on;", Read(name));
        }

        string[] tlsListens = [.. Regex.Matches(Site(), @"(?m)^\s*listen\s+443[^;]*;").Select(m => m.Value.Trim())];
        Assert.NotEmpty(tlsListens);
        Assert.All(tlsListens, listen => Assert.Contains(" ssl http2", listen));
        Assert.Contains("listen 443 ssl http2;", tlsListens);
        Assert.Contains("ssl_reject_handshake on;", Site());
    }

    [Fact]
    public void Internal_Returns404()
    {
        Assert.Matches(@"location \^~ /internal/ \{\s*return 404;\s*\}", Site());
        Assert.Matches(@"location ~\* \^/internal\(/\|\$\) \{\s*return 404;\s*\}", Site());
    }

    [Fact]
    public void Stats_ProxiesExactlyTwoPathsAndLimitsSend()
    {
        string site = Site();
        string[] statsLocations = [.. Regex.Matches(site, @"location\s+([^{]*?/stats[^{]*?)\s*\{").Select(m => m.Groups[1].Value)];
        Assert.Equal(["/stats/", "= /stats", "= /stats/api/send", "= /stats/script.js"], statsLocations.Order(StringComparer.Ordinal));

        Assert.Matches(@"location = /stats \{\s*return 404;\s*\}", site);
        Assert.Matches(@"location /stats/ \{\s*return 404;\s*\}", site);
        Assert.Contains("proxy_pass http://127.0.0.1:3001/script.js;", Location(site, "= /stats/script.js"));

        string send = Location(site, "= /stats/api/send");
        Assert.Contains("limit_req zone=dit_stats burst=20 nodelay;", send);
        Assert.Contains("client_max_body_size 16k;", send);
        Assert.Contains("limit_req_status 429;", send);
        Assert.Contains("proxy_pass http://127.0.0.1:3001/api/send;", send);
        Assert.Contains("limit_req_zone $binary_remote_addr zone=dit_stats:1m rate=10r/s;", site);
    }

    [Fact]
    public void StatsSend_DropsEveryOtherClientIpHeader()
    {
        string send = Location(Site(), "= /stats/api/send");

        foreach (string header in BlankedClientIpHeaders)
        {
            Assert.Contains($"proxy_set_header {header} \"\";", send);
        }
    }

    [Fact]
    public void OnlyDomainAndAppDirPlaceholders()
    {
        foreach (string name in TemplateNames)
        {
            string[] placeholders = [.. Regex.Matches(Read(name), @"\$\{([^}]*)\}").Select(m => m.Groups[1].Value).Distinct()];
            Assert.All(placeholders, p => Assert.Contains(p, AllowedPlaceholders));
        }

        // The brace-less nginx variables must stay out of envsubst's reach: bootstrap restricts it to these two names.
        Assert.Contains("${DOMAIN}", Site());
        Assert.Contains("${APP_DIR}", Site());
    }

    [Fact]
    public void Hsts_IsSet()
    {
        string site = Site();

        Assert.Contains("add_header Strict-Transport-Security \"max-age=31536000\" always;", site);
        Assert.DoesNotContain("preload", site);
        Assert.Contains("ssl_protocols TLSv1.2 TLSv1.3;", site);
        Assert.Contains("server_tokens off;", site);
        Assert.Contains("client_max_body_size 1m;", site);
    }

    [Fact]
    public void HttpOnlyTemplate_ServesOnlyAcmeChallenge()
    {
        string config = Read("dev-interview-trainer.http.conf.template");

        Assert.DoesNotContain("443", config);
        Assert.DoesNotContain("proxy_pass", config);
        Assert.Contains("root /var/www/certbot;", Location(config, "/.well-known/acme-challenge/"));
        Assert.Matches(@"location / \{\s*return 404;\s*\}", config);
        Assert.Equal(2, Regex.Count(config, @"(?m)^\s*location "));
    }
}
