using Pwiz.Data.MsData.Readers;
using Pwiz.Vendor.Thermo;

namespace Glimpse.Pwiz;

/// <summary>
/// Plugs the vendor readers this build carries into pwiz's format dispatcher.
/// <c>Pwiz.Data.MsData</c> knows only the open formats until a host adds the vendor readers.
/// </summary>
internal static class VendorReaders
{
    static VendorReaders()
    {
        // ReaderList.Default copies AdditionalReaders on every access, so register exactly once.
        // Skyline-PRISM registers the same reader in the same way; don't add a second copy if a host
        // process has already done it.
        if (!ReaderList.AdditionalReaders.OfType<Reader_Thermo>().Any())
            ReaderList.AdditionalReaders.Add(new Reader_Thermo());
    }

    /// <summary>Touching the type runs the static constructor; nothing else to do.</summary>
    internal static void EnsureRegistered()
    {
    }
}
