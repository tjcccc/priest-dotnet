namespace Priest.Profile;

/// <summary>
/// Built-in fallback profile.
/// Content must match spec/behavior/profile-loading.md exactly.
/// </summary>
public static class DefaultProfile
{
    public static Profile Instance { get; } = new Profile(
        name:     "default",
        identity: "You are a helpful, thoughtful assistant.\n",
        rules:    "Be honest. Do not make things up.\nBe concise unless the user asks for depth.\n"
    );
}
