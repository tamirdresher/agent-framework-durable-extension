// Copyright (c) Microsoft. All rights reserved.

using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Microsoft.Agents.AI.Foundry;

namespace Microsoft.Agents.AI.DurableTask.Tests.Unit;

public sealed class FoundryAgentRegistrationTests
{
    [Fact]
    public void ServerManagedFoundryAgentCanBeRegisteredWithoutPerCallDeclaration()
    {
        AIProjectClient projectClient = new(
            new Uri("https://example.services.ai.azure.com/api/projects/test"),
            new FakeAuthenticationTokenProvider());
        FoundryAgent foundryAgent =
            projectClient.AsAIAgent(new AgentReference("foundry-managed-agent"));

        ChatClientAgent? innerAgent = foundryAgent.GetService<ChatClientAgent>();
        DurableAgentsOptions options = new();

        options.AddAIAgent(foundryAgent);

        Assert.NotNull(innerAgent);
        Assert.Same(innerAgent, DurableAgentHistoryOwnershipResolver.FindChatClientAgent(foundryAgent));
        Assert.False(options.IsServiceManagedPerServiceCallHistory(foundryAgent.Name!));
    }

    private sealed class FakeAuthenticationTokenProvider : AuthenticationTokenProvider
    {
        public override GetTokenOptions? CreateTokenOptions(
            IReadOnlyDictionary<string, object> properties)
        {
            return new GetTokenOptions(new Dictionary<string, object>());
        }

        public override AuthenticationToken GetToken(
            GetTokenOptions options,
            CancellationToken cancellationToken)
        {
            return new AuthenticationToken(
                "test-token",
                "Bearer",
                DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AuthenticationToken> GetTokenAsync(
            GetTokenOptions options,
            CancellationToken cancellationToken)
        {
            return new(this.GetToken(options, cancellationToken));
        }
    }
}
