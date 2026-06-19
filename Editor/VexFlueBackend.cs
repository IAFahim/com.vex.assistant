using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.AI.Assistant.ApplicationModels;
using Unity.AI.Assistant.Backend;
using Unity.AI.Assistant.Data;
using Unity.AI.Assistant.Socket.ErrorHandling;
using Unity.AI.Assistant.Socket.Workflows.Chat;

namespace Vex.Assistant.Editor
{
    /// <summary>
    /// An <see cref="IAssistantBackend"/> that runs the Assistant window on the user's OWN model via flue, with NO
    /// Unity cloud, relay, or subscription. Injected through AssistantWindow's designed override hook
    /// (InternalConfigureBackend), reachable only because the fork grants us internal access via additive
    /// InternalsVisibleTo friend files.
    ///
    /// Minimal cut: chat is fully functional (each prompt → flue card via <see cref="VexFlueChatWorkflow"/>); the
    /// conversation/model/feedback admin surface is stubbed (no server-side history, no model catalog, no cost).
    /// </summary>
    sealed class VexFlueBackend : IAssistantBackend
    {
        readonly string m_Model; // null => flue default model
        VexFlueChatWorkflow m_Active;

        public VexFlueBackend(string model = null) => m_Model = model;

        public bool SessionStatusTrackingEnabled => false;

        public IChatWorkflow ActiveWorkflow => m_Active;

        public IChatWorkflow GetOrCreateWorkflow(ICredentialsContext credentialsContext, IFunctionCaller caller,
            AssistantConversationId conversationId = default, bool skipInitialization = false)
        {
            // Use the UI's conversation id (not a fresh Guid) so the workflow's state changes are attributed to the
            // ACTIVE conversation — otherwise the UI ignores our Idle→Connected transition and the input spins forever.
            // Empty id (a brand-new conversation) → the workflow generates one and reports it back via OnConversationId.
            var wantId = conversationId.Value;

            // The UI passes an empty id ONLY for a brand-new conversation: Assistant.ProcessPromptInternal sets
            // isNewConversation = !conversationId.IsValid, then adopts workflow.ConversationId for every later turn
            // (BaseChatWorkflow sets it from the discussion-init message). So once m_Active is an ESTABLISHED
            // conversation (non-empty ConversationId), an empty wantId is a "New chat" request — we must spin up a
            // fresh workflow, NOT reuse the old one. Reusing it was the "New chat jumps back to the old conversation"
            // bug: the stale workflow kept its ConversationId, and thus its VexChatHistory transcript + flue session.
            var wantNew = string.IsNullOrEmpty(wantId);
            var needNew =
                m_Active == null ||
                m_Active.WorkflowState == State.Closed ||
                (wantNew && !string.IsNullOrEmpty(m_Active.ConversationId)) ||
                (!wantNew && m_Active.ConversationId != wantId);

            if (needNew)
            {
                m_Active?.Dispose(); // tear down the previous workflow (its event subs + any live flue process)
                m_Active = new VexFlueChatWorkflow(wantId, caller, m_Model);
                m_Active.Start();
            }
            return m_Active;
        }

        // --- Conversation admin: no server-side history in the minimal cut -------------------------------------
        public Task<BackendResult<IEnumerable<ConversationInfo>>> ConversationRefresh(ICredentialsContext c, CancellationToken ct = default)
            => Task.FromResult(BackendResult<IEnumerable<ConversationInfo>>.Success(Array.Empty<ConversationInfo>()));

        public Task<BackendResult<ClientConversation>> ConversationLoad(ICredentialsContext c, string conversationUid, CancellationToken ct = default)
            => Task.FromResult(BackendResult<ClientConversation>.Success(new ClientConversation
            {
                // A well-formed empty conversation (non-null collections + id) so the UI's history parse doesn't fail.
                Id = conversationUid,
                Title = "Vex (flue)",
                Owners = new List<string>(),
                Tags = new List<string>(),
                History = new List<ConversationFragment>(),
            }));

        public Task<BackendResult> ConversationFavoriteToggle(ICredentialsContext c, string conversationUid, bool isFavorite, CancellationToken ct = default)
            => Task.FromResult(BackendResult.Success());

        public Task<BackendResult> ConversationRename(ICredentialsContext c, string conversationUid, string newName, CancellationToken ct = default)
            => Task.FromResult(BackendResult.Success());

        public Task<BackendResult> ConversationDelete(ICredentialsContext c, string conversationUid, CancellationToken ct = default)
            => Task.FromResult(BackendResult.Success());

        public Task<BackendResult<string>> ConversationGenerateTitle(ICredentialsContext c, string conversationId, CancellationToken ct = default)
            => Task.FromResult(BackendResult<string>.Success("Vex (flue)"));

        // --- Feedback / cost: not tracked --------------------------------------------------------------------
        public Task<BackendResult> SendFeedback(ICredentialsContext c, string conversationUid, MessageFeedback feedback, CancellationToken ct = default)
            => Task.FromResult(BackendResult.Success());

        public Task<BackendResult<FeedbackData?>> LoadFeedback(ICredentialsContext c, AssistantMessageId messageId, CancellationToken ct = default)
            => Task.FromResult(BackendResult<FeedbackData?>.Success(null));

        public Task<BackendResult<int?>> FetchMessageCost(ICredentialsContext c, AssistantMessageId messageId, CancellationToken ct = default)
            => Task.FromResult(BackendResult<int?>.Success(0));

        // --- Catalog / version: flue owns the model, so no Unity model catalog; no version constraints --------
        public Task<BackendResult<IReadOnlyList<ModelProfile>>> GetAvailableModelProfiles(ICredentialsContext c, CancellationToken ct = default)
            => Task.FromResult(BackendResult<IReadOnlyList<ModelProfile>>.Success(new List<ModelProfile>()));

        public Task<BackendResult<List<VersionSupportInfo>>> GetVersionSupportInfo(ICredentialsContext c, CancellationToken ct = default)
            => Task.FromResult(BackendResult<List<VersionSupportInfo>>.Success(new List<VersionSupportInfo>()));
    }
}
