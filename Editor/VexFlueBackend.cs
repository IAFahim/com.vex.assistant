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
    internal sealed class VexFlueBackend : IAssistantBackend
    {
        private readonly string m_Model;
        private VexFlueChatWorkflow m_Active;

        public VexFlueBackend(string model = null)
        {
            m_Model = model;
        }

        public bool SessionStatusTrackingEnabled => false;

        public IChatWorkflow ActiveWorkflow => m_Active;

        public IChatWorkflow GetOrCreateWorkflow(ICredentialsContext credentialsContext, IFunctionCaller caller,
            AssistantConversationId conversationId = default, bool skipInitialization = false)
        {
            var wantId = conversationId.Value;

            var wantNew = string.IsNullOrEmpty(wantId);
            var needNew =
                m_Active == null ||
                m_Active.WorkflowState == State.Closed ||
                (wantNew && !string.IsNullOrEmpty(m_Active.ConversationId)) ||
                (!wantNew && m_Active.ConversationId != wantId);

            if (needNew)
            {
                m_Active?.Dispose();
                m_Active = new VexFlueChatWorkflow(wantId, caller, m_Model);
                m_Active.Start();
            }

            return m_Active;
        }

        public Task<BackendResult<IEnumerable<ConversationInfo>>> ConversationRefresh(ICredentialsContext c,
            CancellationToken ct = default)
        {
            return Task.FromResult(
                BackendResult<IEnumerable<ConversationInfo>>.Success(Array.Empty<ConversationInfo>()));
        }

        public Task<BackendResult<ClientConversation>> ConversationLoad(ICredentialsContext c, string conversationUid,
            CancellationToken ct = default)
        {
            return Task.FromResult(BackendResult<ClientConversation>.Success(new ClientConversation
            {
                Id = conversationUid,
                Title = "Vex (flue)",
                Owners = new List<string>(),
                Tags = new List<string>(),
                History = new List<ConversationFragment>()
            }));
        }

        public Task<BackendResult> ConversationFavoriteToggle(ICredentialsContext c, string conversationUid,
            bool isFavorite, CancellationToken ct = default)
        {
            return Task.FromResult(BackendResult.Success());
        }

        public Task<BackendResult> ConversationRename(ICredentialsContext c, string conversationUid, string newName,
            CancellationToken ct = default)
        {
            return Task.FromResult(BackendResult.Success());
        }

        public Task<BackendResult> ConversationDelete(ICredentialsContext c, string conversationUid,
            CancellationToken ct = default)
        {
            return Task.FromResult(BackendResult.Success());
        }

        public Task<BackendResult<string>> ConversationGenerateTitle(ICredentialsContext c, string conversationId,
            CancellationToken ct = default)
        {
            return Task.FromResult(BackendResult<string>.Success("Vex (flue)"));
        }

        public Task<BackendResult> SendFeedback(ICredentialsContext c, string conversationUid, MessageFeedback feedback,
            CancellationToken ct = default)
        {
            return Task.FromResult(BackendResult.Success());
        }

        public Task<BackendResult<FeedbackData?>> LoadFeedback(ICredentialsContext c, AssistantMessageId messageId,
            CancellationToken ct = default)
        {
            return Task.FromResult(BackendResult<FeedbackData?>.Success(null));
        }

        public Task<BackendResult<int?>> FetchMessageCost(ICredentialsContext c, AssistantMessageId messageId,
            CancellationToken ct = default)
        {
            return Task.FromResult(BackendResult<int?>.Success(0));
        }

        public Task<BackendResult<IReadOnlyList<ModelProfile>>> GetAvailableModelProfiles(ICredentialsContext c,
            CancellationToken ct = default)
        {
            return Task.FromResult(BackendResult<IReadOnlyList<ModelProfile>>.Success(new List<ModelProfile>()));
        }

        public Task<BackendResult<List<VersionSupportInfo>>> GetVersionSupportInfo(ICredentialsContext c,
            CancellationToken ct = default)
        {
            return Task.FromResult(BackendResult<List<VersionSupportInfo>>.Success(new List<VersionSupportInfo>()));
        }
    }
}