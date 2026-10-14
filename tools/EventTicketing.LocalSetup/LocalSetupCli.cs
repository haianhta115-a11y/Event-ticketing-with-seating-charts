namespace EventTicketing.LocalSetup;

public static class LocalSetupCli
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length < 3 || args[1] != "--database")
                throw new InvalidOperationException("Usage: init|verify --database NAME; fixture --database NAME --email EMAIL --role Organizer|Admin. ConnectionStrings__DefaultConnection must be explicit.");
            var connection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection") ?? "";
            var target = args[2];
            FreshLocalDatabase.Validate(connection, target);
            switch (args[0])
            {
                case "init" when args.Length == 3:
                    await FreshLocalDatabase.Initialize(connection, target);
                    Console.WriteLine("Created and initialized NEW local target: " + target);
                    break;
                case "verify" when args.Length == 3:
                    Console.WriteLine("Schema verified; SHA-256: " + await FreshLocalDatabase.Verify(connection, target));
                    break;
                case "fixture" when args.Length == 7 && args[3] == "--email" && args[5] == "--role":
                    var password = Environment.GetEnvironmentVariable("LOCAL_DEMO_PASSWORD");
                    if (string.IsNullOrEmpty(password))
                    {
                        if (Console.IsInputRedirected) throw new InvalidOperationException("Supply LOCAL_DEMO_PASSWORD privately for noninteractive fixture provisioning.");
                        Console.Write("New local fixture password (hidden, minimum 12 characters): ");
                        var hidden = new System.Text.StringBuilder();
                        ConsoleKeyInfo key;
                        while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter)
                        {
                            if (key.Key == ConsoleKey.Backspace) { if (hidden.Length > 0) hidden.Length--; }
                            else if (!char.IsControl(key.KeyChar)) hidden.Append(key.KeyChar);
                        }
                        Console.WriteLine();
                        password = hidden.ToString();
                    }
                    await FreshLocalDatabase.Provision(connection, target, args[4], args[6], password);
                    Console.WriteLine("Created active local fixture: " + args[4] + " / " + args[6]);
                    break;
                default: throw new InvalidOperationException("Unknown command/arguments; no operation performed.");
            }
            return 0;
        }
        catch (InvalidOperationException error) { Console.Error.WriteLine(error.Message); return 1; }
        catch { Console.Error.WriteLine("Local setup failed. Details withheld to protect credentials; preserve the target for inspection. No automatic cleanup."); return 1; }
    }
}
