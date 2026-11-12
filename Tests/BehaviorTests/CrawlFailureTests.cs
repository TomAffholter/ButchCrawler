using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ButchCrawler.App;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class CrawlFailureTests
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        var scope=typeof(CrawlerService).GetMethod("IsUrlAllowedBySeedRules",BindingFlags.Static|BindingFlags.NonPublic)!;
        var seed=new SeedUrl(1,"https://www.rihs.org/history","rihs.org",null,0,5,false,true,null,DateTime.UtcNow,null,null,null,null);
        bool Allowed(string target,string parent) => (bool)scope.Invoke(null,new object[]{target,parent,seed})!;
        check(Allowed("https://www.rihs.org/history/","https://www.rihs.org/history"),"seed scope permits trailing slash despite bare-domain seed metadata");
        check(Allowed("https://rihs.org/history/","https://www.rihs.org/history"),"seed scope permits www canonical redirect");
        check(!Allowed("https://evilrihs.org/history/","https://www.rihs.org/history"),"scope still rejects lookalike external domain");
        check(!Allowed("https://archive.rihs.org/history/","https://www.rihs.org/history"),"scope does not implicitly allow arbitrary subdomains");
        check(!Allowed("https://rihs.org:8443/history/","https://www.rihs.org/history"),"scope still rejects different nondefault port");

        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Crawler:AllowPrivateNetwork"]="true",["Crawler:MinDelayMs"]="0",["Crawler:MaxAttempts"]="4",["Crawler:RequestTimeoutSeconds"]="3" }).Build();
        using var http=new HttpClient(); var fetcher=new Fetcher(http,NullLogger<Fetcher>.Instance,config,new NoopCaptchaSolver());
        using var rsa=RSA.Create(2048);
        var req=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var certificate=req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
        var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        using var stop=new CancellationTokenSource(); var connections=0;
        var server=Task.Run(async ()=>
        {
            while(!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client=await listener.AcceptTcpClientAsync(stop.Token); }
                catch(OperationCanceledException) { break; }
                Interlocked.Increment(ref connections);
                using(client) using(var tls=new SslStream(client.GetStream()))
                {
                    try { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate=certificate },stop.Token); }
                    catch(Exception ex) when(ex is System.Security.Authentication.AuthenticationException or System.IO.IOException or OperationCanceledException) { }
                }
            }
        });
        try
        {
            var failed=await fetcher.FetchAsync($"https://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/",default);
            check(!failed.Success && failed.Retryable==false && failed.Error.StartsWith("TLS connection failed:") && failed.Error.Contains("→"),"TLS certificate failure includes underlying reason and is not retried");
            check(connections==1,"TLS failure avoids multiplying immediate and queue retry budgets");
        }
        finally { stop.Cancel(); listener.Stop(); await server; }
    }
}
