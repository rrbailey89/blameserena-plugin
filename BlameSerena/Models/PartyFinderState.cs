namespace BlameSerena.Models;

/// <summary>
/// Manages state for Party Finder listing creation and tracking
/// </summary>
public class PartyFinderState
{
    // Window and interaction state
    public bool CondWindowOpen { get; set; }
    public bool RecruitClicked { get; set; }
    public bool YesClicked { get; set; }
    public bool ConfirmationShown { get; set; }

    // Duplicate detection tracking
    public ulong LastListingId { get; set; }
    public ulong LastDutyId { get; set; }
    public int LastCommentHash { get; set; }

    // Temporary storage for current PF data from StoredRecruitmentInfo
    public ushort TempDutyId { get; set; }
    public string TempComment { get; set; } = string.Empty;
    public ushort TempPwdState { get; set; }
    public byte TempFlags { get; set; }

    /// <summary>
    /// Reset state flags for a new recruit attempt
    /// </summary>
    public void Reset()
    {
        RecruitClicked = false;
        YesClicked = false;
        ConfirmationShown = false;
    }

    /// <summary>
    /// Check if the current listing is a duplicate of the last sent listing
    /// </summary>
    public bool IsDuplicateListing(ulong currentListingId)
    {
        int tempCommentHash = TempComment.GetHashCode();

        if (currentListingId != 0 && currentListingId == LastListingId &&
            TempDutyId == LastDutyId && tempCommentHash == LastCommentHash)
        {
            return true;
        }

        if (currentListingId == 0 && TempDutyId == LastDutyId &&
            tempCommentHash == LastCommentHash && LastListingId != 0)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Update tracking values after successfully sending a listing
    /// </summary>
    public void UpdateLastSent(ulong currentListingId)
    {
        LastDutyId = TempDutyId;
        LastCommentHash = TempComment.GetHashCode();
        if (currentListingId != 0)
            LastListingId = currentListingId;
    }
}
