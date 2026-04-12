namespace Priest.Profiles;

/// <summary>A loaded priest profile.</summary>
public class Profile
{
    public string Name { get; set; }
    public string Identity { get; set; }
    public string Rules { get; set; }
    public string? Custom { get; set; }
    public IList<string> Memories { get; set; }

    public Profile(string name, string identity, string rules, IList<string>? memories = null, string? custom = null)
    {
        Name = name;
        Identity = identity;
        Rules = rules;
        Memories = memories ?? Array.Empty<string>();
        Custom = custom;
    }
}
