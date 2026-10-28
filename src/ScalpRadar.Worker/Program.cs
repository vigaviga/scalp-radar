using ScalpRadar.Core.Options;
using ScalpRadar.Worker;
using ScalpRadar.Worker.Database;
using Serilog;

// A Windows service starts in System32, so anchor relative paths (logs/, appsettings) to the exe folder.
Environment.CurrentDirectory = AppContext.BaseDirectory;
Console.OutputEncoding = System.Text.Encoding.UTF8;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // User secrets load in every environment (not just Development), so a console run on this
    // machine finds the token. The Windows service gets its secrets from environment variables instead.
    builder.Configuration.AddUserSecrets<Program>(optional: true);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args); // command-line flags (e.g. --Telegram:Enabled=false) win over secrets

    builder.Services.AddWindowsService(o => o.ServiceName = "ScalpRadar");
    builder.Services.AddSerilog((_, lc) => lc.ReadFrom.Configuration(builder.Configuration));
    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

    builder.Services.AddScalpRadar(builder.Configuration);

    var host = builder.Build();

    await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();

    await host.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "ScalpRadar stopped because of an unhandled exception");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program;
