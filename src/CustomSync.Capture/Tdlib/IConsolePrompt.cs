namespace CustomSync.Capture.Tdlib;

public interface IConsolePrompt
{
    string? Prompt(string message, bool isSecret = false);
}

public class ConsolePrompt : IConsolePrompt
{
    public string? Prompt(string message, bool isSecret = false)
    {
        Console.Write(message);
        if (!isSecret)
        {
            return Console.ReadLine();
        }

        // Secret input (masking with bullets or not echoing)
        var pass = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (pass.Length > 0)
                {
                    pass.Length--;
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                pass.Append(key.KeyChar);
                Console.Write("*");
            }
        }

        return pass.ToString();
    }
}
