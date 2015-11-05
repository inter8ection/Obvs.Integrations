﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Obvs.Integrations.Slack.Api;

namespace Obvs.Integrations.Slack.Bot
{
    internal interface ISlackBot : IDisposable
    {
        Task Connect();
        Task Disconnect();
        void RegisterHandler(Handler handler);
        Channel[] GetChannels();
        User[] GetUsers();
    }

    internal class SlackBot : Bot, ISlackBot
    {
		private readonly ISlackRestApi _api;
		private readonly ClientWebSocket _webSocket = new ClientWebSocket();

        private User _self;
        private readonly Dictionary<string, User> _users = new Dictionary<string, User>(); // TODO: Handle new users joining/leaving
        private readonly Dictionary<string, Channel> _channels = new Dictionary<string, Channel>(); // TODO: Handle new channels/deleted

	    private SlackBot(string token)
		{
			_api = new SlackRestApi(token);
		}

		public static async Task<SlackBot> Connect(string apiToken)
		{
			// Can't do async constructors, so do connection here. This makes it easy to tie the lifetime of the
			// websocket to this class.
			var bot = new SlackBot(apiToken);
			await bot.Connect();
			return bot;
		}

	    public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
		    if (disposing)
		    {
		        _webSocket.Dispose();
		    }
		}

        private Task<AuthTestResponse> AuthTest() => _api.Post<AuthTestResponse>("auth.test");

        private Task<RtmStartResponse> RtmStart() => _api.Post<RtmStartResponse>("rtm.start");

        private Task<PostMessageResponse> PostMessage(string channelId, string text, Attachment[] attachments = null) =>
			_api.Post<PostMessageResponse>("chat.postMessage", new Dictionary<string, string> {
				{ "as_user", "true" },
				{ "channel", channelId },
				{ "text", text },
				{ "attachments", attachments != null ? Serialiser.Serialise(attachments) : "" }
			});

	    public async Task Connect()
		{
	        if (_webSocket.State == WebSocketState.Connecting || _webSocket.State == WebSocketState.Open)
	        {
	            throw new InvalidOperationException("Bot is already connected");
	        }

			// First check we can authenticate.
			var authResponse = await AuthTest();
			Debug.WriteLine("Authorised as " + authResponse.User);

			// Issue a request to start a real time messaging session.
			var rtmStartResponse = await RtmStart();

			// Store users and channels so we can look them up by ID.
			_self = rtmStartResponse.Self;
	        foreach (var user in rtmStartResponse.Users)
	        {
	            _users.Add(user.ID, user);
	        }
	        foreach (var channel in rtmStartResponse.Channels.Union(rtmStartResponse.IMs))
	        {
	            _channels.Add(channel.ID, channel);
	        }

			// Connect the WebSocket to the URL we were given back.
			await _webSocket.ConnectAsync(rtmStartResponse.Url, CancellationToken.None);
			Debug.WriteLine("Connected...");

			// Start the receive message loop.
			var _ = Task.Run(ListenForApiMessages);

			// Say hello in each of the channels the bot is a member of.
	        foreach (var channel in _channels.Values.Where(c => !c.IsPrivate && c.IsMember))
	        {
	            await SayHello(channel);
	        }
		}

		public async Task Disconnect()
		{
			// Cancel all in-process tasks.
			await CancelAllTasks();

			// Say goodbye to each of the channels the bot is a member of.
		    foreach (var channel in _channels.Values.Where(c => !c.IsPrivate && c.IsMember))
		    {
		        await SayGoodbye(channel);
		    }
		}

        public Channel[] GetChannels()
        {
            return _channels.Values.ToArray();
        }

        public User[] GetUsers()
        {
            return _users.Values.ToArray();
        }

        private async Task ListenForApiMessages()
		{
			var buffer = new byte[1024];
			var segment = new ArraySegment<byte>(buffer);
			while (_webSocket.State == WebSocketState.Open)
			{
				var fullMessage = new StringBuilder();

				while (true)
				{
					var msg = await _webSocket.ReceiveAsync(segment, CancellationToken.None);

					fullMessage.Append(Encoding.UTF8.GetString(buffer, 0, msg.Count));
				    if (msg.EndOfMessage)
				    {
				        break;
				    }
				}

				await HandleApiMessage(fullMessage.ToString());
			}
		}

	    internal override async Task SendMessage(Channel channel, string text, Attachment[] attachments = null)
		{
			try
			{
				await PostMessage(channel.ID, text, attachments);
			}
			catch (Exception ex)
			{
				Debug.WriteLine(ex.ToString());
			}
		}

		internal override async Task SendTypingIndicator(Channel channel)
		{
			await SendApiMessage(new TypingIndicator(channel.ID));
		}

		internal async Task SendApiMessage<T>(T message)
		{
			var json = Serialiser.Serialise(message);
			Debug.WriteLine("SEND: " + json);
			await _webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, CancellationToken.None);
		}

        private async Task HandleApiMessage(string message)
		{
			Debug.WriteLine("RCV: " + message);

			var eventType = Serialiser.Deserialise<Event>(message).Type;

			switch (eventType)
			{
				case MessageEvent.TYPE:
					await HandleApiMessage(Serialiser.Deserialise<MessageEvent>(message));
					break;

				case ChannelChangedEvent.CHANNEL_CHANGED_TYPE:
				case ChannelChangedEvent.CHANNEL_CREATED_TYPE:
					HandleApiMessage(Serialiser.Deserialise<ChannelChangedEvent>(message));
					break;

				case UserChangedEvent.USER_CHANGED_TYPE:
				case UserChangedEvent.USER_CREATED_TYPE:
					HandleApiMessage(Serialiser.Deserialise<UserChangedEvent>(message));
					break;

				case ChannelJoinedEvent.TYPE:
					await HandleApiMessage(Serialiser.Deserialise<ChannelJoinedEvent>(message));
					break;
			}
		}

        private async Task HandleApiMessage(MessageEvent message)
		{
			var channelId = message.Message?.ChannelID ?? message.ChannelID;
			var userId = message.Message?.UserID ?? message.UserID;
			var text = message.Message?.Text ?? message.Text;
            
		    var messageIsFromBot = userId == _self.ID;
		    if (messageIsFromBot)
		    {
		        return;
		    }

			var botIsMentioned = text.Contains($"<@{_self.ID}>");

			HandleRecievedMessage(_channels[channelId], _users[userId], text, botIsMentioned);

			await Task.FromResult(true);
		}

        private async Task HandleApiMessage(ChannelJoinedEvent message)
		{
			Debug.WriteLine("JOINED: " + message.Channel.Name);

			await SayHello(message.Channel);
		}

        private void HandleApiMessage(ChannelChangedEvent message)
		{
			Debug.WriteLine("CHANNEL UPDATED: " + message.Channel.Name);

			_channels[message.Channel.ID] = message.Channel;
		}

        private void HandleApiMessage(UserChangedEvent message)
		{
			Debug.WriteLine("USER UPDATED: " + message.User.Name);

			_users[message.User.ID] = message.User;
		}
	}
}