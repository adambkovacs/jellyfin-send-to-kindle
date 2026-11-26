using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SendToKindle.Delivery;

internal static class DeliveryBookAccess
{
    public static Book? Find(ILibraryManager libraryManager, User user, Guid itemId)
    {
        // The user-scoped lookup applies Jellyfin's item, parent and library visibility rules.
        return libraryManager.GetItemById<Book>(itemId, user) is { IsVirtualItem: false, Path: { Length: > 0 } path } book
            && Path.IsPathFullyQualified(path)
            && book.CanDownload(user)
            && File.Exists(path)
            ? book
            : null;
    }
}
