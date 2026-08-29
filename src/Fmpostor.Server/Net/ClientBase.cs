using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Fmpostor.Api;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Innersloth.Customization;
using Fmpostor.Api.Net;
using Fmpostor.Server.Net.State;

namespace Fmpostor.Server.Net
{
    internal abstract class ClientBase : IClient
    {
        protected ClientBase(string name, GameVersion gameVersion, Language language, QuickChatModes chatMode, PlatformSpecificData platformSpecificData, IHazelConnection connection)
        {
            Name = name;
            GameVersion = gameVersion;
            Language = language;
            ChatMode = chatMode;
            PlatformSpecificData = platformSpecificData;
            Connection = connection;
            Items = new ConcurrentDictionary<object, object>();
            FriendCode = string.Empty;
            ProductUserId = string.Empty;
        }

        public int Id { get; set; }

        public string Name { get; }

        public Language Language { get; }

        public QuickChatModes ChatMode { get; }

        public PlatformSpecificData PlatformSpecificData { get; }

        public GameVersion GameVersion { get; }

        public IHazelConnection Connection { get; }

        public IDictionary<object, object> Items { get; }

        public string FriendCode { get; set; }

        public string ProductUserId { get; set; }

        public string Puid
        {
            get => ProductUserId;
            set => ProductUserId = value;
        }

        public ClientPlayer? Player { get; set; }

        public ColorType? PreviousColor { get; set; } = null;

        IClientPlayer? IClient.Player => Player;

        public virtual ValueTask<bool> ReportCheatAsync(CheatContext context, CheatCategory category, string message)
        {
            return new ValueTask<bool>(false);
        }

        public ValueTask<bool> ReportCheatAsync(CheatContext context, string message)
        {
            return ReportCheatAsync(context, CheatCategory.Other, message);
        }

        public abstract ValueTask HandleMessageAsync(IMessageReader message, MessageType messageType);

        public abstract ValueTask HandleDisconnectAsync(string reason);

        public async ValueTask DisconnectAsync(DisconnectReason reason, string? message = null)
        {
            await Connection.CustomDisconnectAsync(reason, message);
        }

        public bool Equals(IClient? other)
        {
            return other != null && Id == other.Id;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as ClientBase);
        }

        public override int GetHashCode()
        {
            return Id;
        }
    }
}
