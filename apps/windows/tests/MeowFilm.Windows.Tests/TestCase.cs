namespace MeowFilm.Windows.Tests;

public sealed record TestCase(string Name, Func<Task> Run);
