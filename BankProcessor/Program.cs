using BisUtils.RVBank.Model;
using BisUtils.RVBank.Options;
using Mono.Options;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

public sealed class Program
{
    public static Program Instance = null!;
    private static int currentLogLevel = 6;
    private readonly string inputPath;
    private readonly RVBankOptions options;
    private readonly IEnumerable<RVBank> banks;

    public static void Main(string[] arguments)
    {
        var inputPath = string.Empty;
        var commandOptions = new OptionSet()
            .Add("v", "Increase verbosity", e => --currentLogLevel)
            .Add("folder", "Input Folder", s => inputPath = s);
        var logSink = Path.GetTempFileName();
#pragma warning disable CA1305
        Log.Logger = new LoggerConfiguration().WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}", restrictedToMinimumLevel: (LogEventLevel)currentLogLevel)
#pragma warning restore CA1305
            .WriteTo
            .File(new JsonFormatter(), logSink, restrictedToMinimumLevel: (LogEventLevel)currentLogLevel).CreateLogger();

        if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
        {
            Log.Fatal("Could not find a file nor a directory at '{inputPath}'", inputPath);
        }

        var options = new RVBankOptions()
        {

        };

        Instance = File.Exists(inputPath) ? new Program(new[] { RVBank.ReadPbo(inputPath, options, syncTo: File.OpenWrite(inputPath)) }, inputPath) : new Program(DiscoverBanks(inputPath, options), inputPath);
    }

    private static IEnumerable<RVBank> DiscoverBanks(string inputPath, RVBankOptions options) =>
        Directory.EnumerateFiles(inputPath, "*.pbo", SearchOption.AllDirectories).Select(it => RVBank.ReadPbo(it, options, File.OpenWrite(it)));
    
    private Program(IEnumerable<RVBank> banks, string inputPath)
    {
        this.banks = banks;
        this.inputPath = inputPath;
    }

}
