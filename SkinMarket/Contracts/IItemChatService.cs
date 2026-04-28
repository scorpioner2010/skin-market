using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IItemChatService
{
    Task<ItemChatThread?> GetOrCreateThreadAsync(Guid appUserId, Guid serviceItemId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ItemChatThreadSummary>> GetUserThreadsAsync(Guid appUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ItemChatThreadSummary>> GetAdminThreadsAsync(CancellationToken cancellationToken = default);
    Task<int> CountUserUnreadThreadsAsync(Guid appUserId, CancellationToken cancellationToken = default);
    Task<int> CountAdminUnreadThreadsAsync(CancellationToken cancellationToken = default);
    Task<ItemChatConversation?> GetUserConversationAsync(Guid appUserId, Guid? threadId, CancellationToken cancellationToken = default);
    Task<ItemChatConversation?> GetAdminConversationAsync(Guid? threadId, CancellationToken cancellationToken = default);
    Task<ItemChatSendResult> SendUserMessageAsync(Guid appUserId, Guid threadId, string body, CancellationToken cancellationToken = default);
    Task<ItemChatSendResult> SendAdminMessageAsync(Guid adminAppUserId, Guid threadId, string body, CancellationToken cancellationToken = default);
    Task<ItemChatDeleteResult> DeleteAdminThreadAsync(Guid threadId, CancellationToken cancellationToken = default);
}
