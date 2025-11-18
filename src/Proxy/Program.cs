using Proxy;

var builder = WebApplication.CreateBuilder(args);
ProxyHost.ConfigureServices(builder);

var app = builder.Build();
ProxyHost.ConfigurePipeline(app);

app.Run();

public partial class Program;
