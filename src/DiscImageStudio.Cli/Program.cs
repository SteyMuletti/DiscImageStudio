using DiscImageStudio.Cd;
using DiscImageStudio.Core;

namespace DiscImageStudio.Cli;

internal static class Program
{
    private static readonly CdDiscModule Cd = new();

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            Console.WriteLine("mixtape-cd | music tracks + visible disc art on one audio CD");
            Console.WriteLine("Usage: mixtape-cd COMMAND [--option value ...]\n");
            foreach (DiscCommandDescriptor command in Cd.Descriptor.Commands)
                Console.WriteLine($"  {command.Name,-18} {command.DisplayName}");
            Console.WriteLine("\nOptions for a command: mixtape-cd COMMAND --help");
            return 0;
        }

        if (args.Length > 1 && args[1] is "--help" or "-h")
        {
            DiscCommandDescriptor? command = Cd.Descriptor.Commands.FirstOrDefault(c => c.Name == args[0]);
            if (command is null) { Console.Error.WriteLine($"Unknown command '{args[0]}'."); return 1; }
            Console.WriteLine($"{command.Name}: {command.Description}\n");
            foreach (DiscOptionDefinition option in command.Options)
                Console.WriteLine($"  --{option.Name,-22} {option.DisplayName}" + (option.Unit is { } u ? $" ({u})" : "")
                    + (option.Required ? " (required)" : option.DefaultValue is { Length: > 0 } d ? $" [default {d}]" : ""));
            return 0;
        }

        using CancellationTokenSource cancel = new();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        try
        {
            DiscJobResult result = Cd.ExecuteAsync(new DiscJobRequest(args[0], args.Skip(1)),
                new Progress(), cancel.Token).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(result.Summary)) Console.WriteLine(result.Summary);
            return result.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"error: {error.Message}");
            return 1;
        }
    }

    private sealed class Progress : IProgress<DiscJobProgress>
    {
        private string _last = "";
        public void Report(DiscJobProgress value)
        {
            // Print at most once per whole percent to keep the console readable.
            string line = value.Fraction is { } f ? $"{value.Stage}: {f:P0}" : value.Message ?? "";
            if (line.Length == 0 || line == _last) return;
            _last = line;
            Console.WriteLine(line);
        }
    }
}
