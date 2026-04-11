namespace Priest.Profile;

/// <summary>Loads a named profile synchronously.</summary>
public interface IProfileLoader
{
    Profile Load(string name);
}
