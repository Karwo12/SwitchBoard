using System.Text.Json.Serialization;

namespace SwitchBoard.Models.Profiles;

/// <summary>
/// Describes an optional user-selected profile icon without embedding image data in the catalog.
/// </summary>
public sealed class ProfileIconSourceDefinition
{
    public const string FileSourceType = "file";
    public const string ActionSourceType = "action";

    /// <summary>Either <see cref="FileSourceType"/> or <see cref="ActionSourceType"/>.</summary>
    public string Type { get; set; } = FileSourceType;

    /// <summary>Full path to an EXE or ICO file selected by the user when <see cref="Type"/> is file.</summary>
    public string? Path { get; set; }

    /// <summary>Stable ID of the profile action used when <see cref="Type"/> is action.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ActionId { get; set; }
}
