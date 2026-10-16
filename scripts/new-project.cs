#!/usr/bin/env dotnet
// Creates a new solution from this base with your own name.
//
//   dotnet run scripts/new-project.cs -- Acme.Payroll ../Acme.Payroll
//
// Copies the repository (without build output, git history or local data), renames the Base.* projects,
// namespaces and solution to <Name>.*, and gives the web project a fresh user-secrets id so projects never
// share secrets. Identifiers that merely contain "Base" (EntityBase, ControllerBase, Database) are untouched.

using System.Text.RegularExpressions;

var timeout = TimeSpan.FromSeconds(1);

if (args.Length != 2 || !Regex.IsMatch(args[0], @"^[A-Z][A-Za-z0-9]*(\.[A-Z][A-Za-z0-9]*)*$", RegexOptions.ExplicitCapture, timeout))
{
    Console.Error.WriteLine("Usage: dotnet run scripts/new-project.cs -- <Company.Product> <target-directory>");
    Console.Error.WriteLine("Name must be PascalCase segments separated by dots, e.g. Acme.Payroll.");
    return 1;
}

var name = args[0];
var source = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory(), ".."));
var target = Path.GetFullPath(args[1]);
if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
{
    Console.Error.WriteLine($"Target '{target}' exists and is not empty.");
    return 1;
}

string[] skippedDirectories = ["bin", "obj", ".git", ".vs", ".idea", ".data", "logs", "TestResults", "node_modules"];
string[] skippedFiles = ["docs/base-project-blueprint.md", "scripts/new-project.cs"];
string[] binaryExtensions = [".png", ".jpg", ".jpeg", ".gif", ".ico", ".woff", ".woff2", ".pdf", ".map"];

// "Base" followed by a project suffix, not preceded by a letter or digit: Base.Domain, ..\Base.Web\, "Base.UnitTests".
var projectToken = new Regex(@"(?<![A-Za-z0-9_])Base(?=\.(Domain|Application|Infrastructure|Web|UnitTests|IntegrationTests|ArchitectureTests|slnx)\b)", RegexOptions.ExplicitCapture, timeout);
var productToken = new Regex(@"(?<=<(Company|Product)>)Base(?=</)|(?<=SetApplicationName\("")Base(?=""\))", RegexOptions.ExplicitCapture, timeout);
var userSecretsId = new Regex("<UserSecretsId>[^<]+</UserSecretsId>", RegexOptions.None, timeout);

var copied = 0;
foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
{
    var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
    if (relative.Split('/').Any(part => skippedDirectories.Contains(part)) || skippedFiles.Contains(relative))
    {
        continue;
    }

    var destination = Path.Combine(target, projectToken.Replace(relative, name));
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

    if (binaryExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()) || relative.StartsWith("src/Base.Web/wwwroot/lib/", StringComparison.Ordinal))
    {
        File.Copy(file, destination);
    }
    else
    {
        var text = File.ReadAllText(file);
        text = projectToken.Replace(text, name);
        text = productToken.Replace(text, name);
        text = userSecretsId.Replace(text, $"<UserSecretsId>{name.ToLowerInvariant()}-{Guid.NewGuid()}</UserSecretsId>");
        File.WriteAllText(destination, text);
    }

    copied++;
}

Console.WriteLine($"Created {name} in {target} ({copied} files).");
Console.WriteLine("Next:");
Console.WriteLine($"  cd {target}");
Console.WriteLine("  git init && dotnet tool restore && dotnet build && dotnet test");
Console.WriteLine("  Review CLAUDE.md and README.md, then replace the initial migration if you change the model before first release.");
return 0;
