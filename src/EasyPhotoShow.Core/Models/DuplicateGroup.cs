namespace EasyPhotoShow.Core.Models;

// Classifies how a duplicate group was formed during detection. Drives the two-track
// review experience: ExactOnly groups can be auto-resolved silently because the user
// is unlikely to want any of the byte-identical copies kept; HasVisualComponent groups
// require human judgment because perceptual similarity is fuzzy and personal.
//
// "Bridging" rule: if a group contains both exact and visual relationships (e.g. photo A
// is an exact copy of B, and B is visually similar to C), classify as HasVisualComponent.
// Auto-resolving a bridged group would silently delete a photo the user might have wanted
// to compare against the visually-similar one.
public enum GroupClassification
{
    ExactOnly,
    HasVisualComponent
}

public sealed class DuplicateGroup
{
    public required IReadOnlyList<Photo> Photos { get; init; }
    public required Photo Recommended { get; init; }
    public GroupClassification Classification { get; init; } = GroupClassification.HasVisualComponent;
}
