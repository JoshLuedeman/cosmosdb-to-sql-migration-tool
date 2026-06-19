namespace CosmosToSqlAssessment.Interactive;

/// <summary>
/// Production implementation of <see cref="IWizardConsole"/> using
/// <see cref="Console.ReadLine"/> and <see cref="Console.WriteLine(string)"/>.
/// </summary>
internal sealed class SystemWizardConsole : IWizardConsole
{
    public string Prompt(string message, string? defaultValue = null)
    {
        if (defaultValue != null)
        {
            Console.Write($"{message} [{defaultValue}]: ");
        }
        else
        {
            Console.Write($"{message}: ");
        }

        var input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input))
        {
            return defaultValue ?? string.Empty;
        }

        return input.Trim();
    }

    public T Select<T>(string title, IReadOnlyList<T> choices) where T : notnull
    {
        Console.WriteLine(title);
        for (int i = 0; i < choices.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {choices[i]}");
        }

        while (true)
        {
            Console.Write($"Enter choice (1-{choices.Count}): ");
            var input = Console.ReadLine();

            if (int.TryParse(input, out int selection) && selection >= 1 && selection <= choices.Count)
            {
                return choices[selection - 1];
            }

            Console.WriteLine($"  Invalid selection. Please enter a number between 1 and {choices.Count}.");
        }
    }

    public bool Confirm(string message, bool defaultValue = true)
    {
        var hint = defaultValue ? "[Y/n]" : "[y/N]";
        Console.Write($"{message} {hint}: ");

        var input = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(input))
        {
            return defaultValue;
        }

        return input == "y" || input == "yes";
    }

    public void WriteInfo(string message)
    {
        Console.WriteLine(message);
    }

    public void WriteError(string message)
    {
        Console.WriteLine($"❌ {message}");
    }

    public void WriteLine()
    {
        Console.WriteLine();
    }

    public string PromptWithValidation(string message, Func<string, string?> validator, string? defaultValue = null)
    {
        while (true)
        {
            var input = Prompt(message, defaultValue);
            var error = validator(input);
            if (error == null)
                return input;
            WriteError(error);
        }
    }
}
