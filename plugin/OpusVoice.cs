using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Carbon.Components;
using Network;
using Oxide.Core;
using UnityEngine;

namespace Carbon.Plugins
{
	[Info("OpusVoice", "OpusConverter", "1.1.0")]
	[Description("Plays audio converted by OpusConverter (.rvoice) through a speaker NPC that sits on an invisible chair attached to each listener (Carbon ClientEntity + Steam voice packets).")]
	public class OpusVoice : CarbonPlugin
	{
		#region Constants

		private const string PermUse = "opusvoice.use";
		private const string FileExtension = ".rvoice";

		// Steam accounts above 0xF0000000 do not exist, so a fake speaker never collides with a real player.
		private const ulong SteamIdBase = 76561197960265728UL + 0xF0000000UL;

		private const string SpeakerPrefab = "assets/prefabs/player/player.prefab";
		private const string ChairPrefab = "assets/bundled/prefabs/static/chair.invisible.static.prefab";

		// The chair is attached to the listener 5 meters below and 3 meters behind them, so the NPC stays out of sight.
		private static readonly Vector3 ChairOffset = new Vector3(0f, -5f, -3f);

		private const float NearRadius = 30f;

		// 3 Opus frames = 60 ms of audio per voice packet.
		private const int FramesPerPacket = 3;

		// Audio is sent this far ahead of real time; the NPC is removed this long after the last frame is due.
		private const double LeadSeconds = 1;

		#endregion

		#region Fields

		private string _dataDir;

		private readonly Dictionary<string, VoiceFile> _cache = new Dictionary<string, VoiceFile>(StringComparer.OrdinalIgnoreCase);
		private readonly List<VoiceStream> _streams = new List<VoiceStream>();
		private int _nextStreamId = 1;
		private uint _nextSpeakerId = 1;

		private Oxide.Plugins.Timer _tickTimer;
		private readonly byte[] _packetBuffer = new byte[SteamVoicePacker.MaxPacketSize(10)];

		private static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

		private static double Now => Stopwatch.GetTimestamp() * TicksToSeconds;

		#endregion

		#region Localization

		protected override void LoadDefaultMessages()
		{
			lang.RegisterMessages(new Dictionary<string, string>
			{
				["NoPermission"] = "You do not have permission to use this command.",
				["Usage"] = "Usage: /vplay <file> [me|all|near|player] | /vstop [id|all] | /vlist",
				["FileNotFound"] = "Voice file '{0}' was not found. Put converted .rvoice files into {1}.",
				["FileInvalid"] = "Voice file '{0}' is invalid: {1}",
				["NeedPlayer"] = "This target needs a player. Use 'all' or a player name from the console.",
				["TargetNotFound"] = "Player '{0}' was not found.",
				["TargetAmbiguous"] = "Several players match '{0}'. Be more specific or use the SteamID.",
				["NoListeners"] = "There is nobody to play the audio for.",
				["SpawnFailed"] = "Could not create the speaker entities. Check that the prefabs exist in this game version.",
				["Started"] = "Stream #{0} started: {1} ({2}, {3} listeners).",
				["Stopped"] = "Stopped {0} stream(s).",
				["NothingToStop"] = "No matching streams are playing.",
				["ListHeader"] = "Voice files ({0}):",
				["ListEntry"] = "  {0} - {1}, {2} kB",
				["ListNoFiles"] = "  (none)",
				["ListStreams"] = "Playing:",
				["ListStream"] = "  #{0} {1} - {2}/{3}, {4} listeners",
			}, this);

			lang.RegisterMessages(new Dictionary<string, string>
			{
				["NoPermission"] = "У вас нет прав на эту команду.",
				["Usage"] = "Использование: /vplay <файл> [me|all|near|игрок] | /vstop [id|all] | /vlist",
				["FileNotFound"] = "Файл '{0}' не найден. Положите сконвертированные .rvoice в {1}.",
				["FileInvalid"] = "Файл '{0}' повреждён: {1}",
				["NeedPlayer"] = "Для этой цели нужен игрок. Из консоли используйте 'all' или ник игрока.",
				["TargetNotFound"] = "Игрок '{0}' не найден.",
				["TargetAmbiguous"] = "Под '{0}' подходит несколько игроков. Уточните ник или используйте SteamID.",
				["NoListeners"] = "Некому проигрывать звук.",
				["SpawnFailed"] = "Не удалось создать NPC и стул. Проверьте, что префабы есть в этой версии игры.",
				["Started"] = "Поток #{0} запущен: {1} ({2}, слушателей: {3}).",
				["Stopped"] = "Остановлено потоков: {0}.",
				["NothingToStop"] = "Подходящих потоков нет.",
				["ListHeader"] = "Голосовые файлы ({0}):",
				["ListEntry"] = "  {0} - {1}, {2} КБ",
				["ListNoFiles"] = "  (нет)",
				["ListStreams"] = "Играет:",
				["ListStream"] = "  #{0} {1} - {2}/{3}, слушателей: {4}",
			}, this, "ru");
		}

		private string Lang(string key, string userId = null, params object[] args)
		{
			string text = lang.GetMessage(key, this, userId);
			return args.Length == 0 ? text : string.Format(text, args);
		}

		#endregion

		#region Oxide/Carbon hooks

		private void Init()
		{
			permission.RegisterPermission(PermUse, this);
			_dataDir = Path.Combine(Interface.Oxide.DataDirectory, "OpusVoice");
			Directory.CreateDirectory(_dataDir);
		}

		private void Unload()
		{
			StopAll();
			_cache.Clear();
		}

		private void OnPlayerDisconnected(BasePlayer player, string reason) => RemovePlayer(player);

		private void OnPlayerDeath(BasePlayer player, HitInfo info) => RemovePlayer(player);

		#endregion

		#region Commands

		[ChatCommand("vplay")]
		private void CmdPlay(BasePlayer player, string command, string[] args)
		{
			if (!HasAccess(player))
			{
				player.ChatMessage(Lang("NoPermission", player.UserIDString));
				return;
			}

			if (args.Length < 1)
			{
				player.ChatMessage(Lang("Usage", player.UserIDString));
				return;
			}

			player.ChatMessage(PlayForSelector(args[0], args.Length > 1 ? args[1] : null, player, player.UserIDString));
		}

		[ChatCommand("vstop")]
		private void CmdStop(BasePlayer player, string command, string[] args)
		{
			if (!HasAccess(player))
			{
				player.ChatMessage(Lang("NoPermission", player.UserIDString));
				return;
			}

			player.ChatMessage(StopByArgument(args.Length > 0 ? args[0] : null, player.UserIDString));
		}

		[ChatCommand("vlist")]
		private void CmdList(BasePlayer player, string command, string[] args)
		{
			if (!HasAccess(player))
			{
				player.ChatMessage(Lang("NoPermission", player.UserIDString));
				return;
			}

			player.ChatMessage(BuildList(player.UserIDString));
		}

		[ConsoleCommand("opusvoice.play")]
		private void ConsolePlay(ConsoleSystem.Arg arg)
		{
			if (!HasAccess(arg))
			{
				arg.ReplyWith(Lang("NoPermission"));
				return;
			}

			// opusvoice.play <file> [me|all|near|player]
			int argc = arg.Args != null ? arg.Args.Length : 0;
			if (argc < 1)
			{
				arg.ReplyWith(Lang("Usage"));
				return;
			}

			arg.ReplyWith(PlayForSelector(arg.GetString(0), argc > 1 ? arg.GetString(1) : null, arg.Player(), null));
		}

		[ConsoleCommand("opusvoice.stop")]
		private void ConsoleStop(ConsoleSystem.Arg arg)
		{
			if (!HasAccess(arg))
			{
				arg.ReplyWith(Lang("NoPermission"));
				return;
			}

			arg.ReplyWith(StopByArgument(arg.Args != null && arg.Args.Length > 0 ? arg.GetString(0) : null, null));
		}

		[ConsoleCommand("opusvoice.list")]
		private void ConsoleList(ConsoleSystem.Arg arg)
		{
			if (!HasAccess(arg))
			{
				arg.ReplyWith(Lang("NoPermission"));
				return;
			}

			arg.ReplyWith(BuildList(null));
		}

		private bool HasAccess(BasePlayer player)
		{
			return player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermUse);
		}

		private bool HasAccess(ConsoleSystem.Arg arg)
		{
			BasePlayer player = arg.Player();
			return player == null || HasAccess(player);
		}

		private string PlayForSelector(string file, string selector, BasePlayer caller, string userId)
		{
			string error;
			List<BasePlayer> targets = ResolveTargets(selector, caller, out error);
			if (targets == null)
			{
				return error;
			}

			VoiceStream stream = Play(file, targets, out error);
			if (stream == null)
			{
				return error;
			}

			return Lang("Started", userId, stream.Id, stream.File.Name, FormatTime(stream.File.Duration), stream.Listeners.Count);
		}

		private string StopByArgument(string argument, string userId)
		{
			int stopped;
			if (string.IsNullOrEmpty(argument) || argument.Equals("all", StringComparison.OrdinalIgnoreCase))
			{
				stopped = _streams.Count;
				StopAll();
			}
			else
			{
				int id;
				stopped = int.TryParse(argument, out id) && StopStream(id) ? 1 : 0;
			}

			return stopped == 0 ? Lang("NothingToStop", userId) : Lang("Stopped", userId, stopped);
		}

		private string BuildList(string userId)
		{
			var sb = new StringBuilder();
			string[] files = Directory.GetFiles(_dataDir, "*" + FileExtension);
			Array.Sort(files, StringComparer.OrdinalIgnoreCase);
			sb.AppendLine(Lang("ListHeader", userId, files.Length));
			if (files.Length == 0)
			{
				sb.AppendLine(Lang("ListNoFiles", userId));
			}

			foreach (string path in files)
			{
				string name = Path.GetFileNameWithoutExtension(path);
				string error;
				VoiceFile file = LoadFile(name, out error);
				string duration = file != null ? FormatTime(file.Duration) : "?";
				sb.AppendLine(Lang("ListEntry", userId, name, duration, new FileInfo(path).Length / 1024));
			}

			if (_streams.Count > 0)
			{
				sb.AppendLine(Lang("ListStreams", userId));
				double now = Now;
				foreach (VoiceStream s in _streams)
				{
					double played = Math.Max(0, Math.Min(now - s.T0, s.File.Duration));
					sb.AppendLine(Lang("ListStream", userId, s.Id, s.File.Name, FormatTime(played), FormatTime(s.File.Duration), s.Listeners.Count));
				}
			}

			return sb.ToString().TrimEnd();
		}

		private static string FormatTime(double seconds)
		{
			TimeSpan t = TimeSpan.FromSeconds(seconds);
			return string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
		}

		#endregion

		#region Targets

		private static bool CanListen(BasePlayer player)
		{
			return player != null && player.net != null && player.Connection != null && player.Connection.connected && !player.IsDead() && !player.IsSleeping();
		}

		/// <summary>
		/// Selectors: me (default), all, near or near:&lt;meters&gt; (players around the caller), or a player name / SteamID.
		/// </summary>
		private List<BasePlayer> ResolveTargets(string selector, BasePlayer caller, out string error)
		{
			error = null;
			var result = new List<BasePlayer>();
			string s = string.IsNullOrWhiteSpace(selector) ? "me" : selector.Trim();

			if (s.Equals("all", StringComparison.OrdinalIgnoreCase))
			{
				foreach (BasePlayer player in BasePlayer.activePlayerList)
				{
					result.Add(player);
				}
			}
			else if (s.Equals("me", StringComparison.OrdinalIgnoreCase) || s.Equals("self", StringComparison.OrdinalIgnoreCase))
			{
				if (caller == null)
				{
					error = Lang("NeedPlayer");
					return null;
				}

				result.Add(caller);
			}
			else if (s.StartsWith("near", StringComparison.OrdinalIgnoreCase))
			{
				if (caller == null)
				{
					error = Lang("NeedPlayer");
					return null;
				}

				float radius;
				int colon = s.IndexOf(':');
				if (colon >= 0 && float.TryParse(s.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out radius))
				{
					radius = Mathf.Clamp(radius, 1f, 500f);
				}
				else
				{
					radius = NearRadius;
				}

				float radiusSqr = radius * radius;
				foreach (BasePlayer player in BasePlayer.activePlayerList)
				{
					if ((player.transform.position - caller.transform.position).sqrMagnitude <= radiusSqr)
					{
						result.Add(player);
					}
				}
			}
			else
			{
				BasePlayer exact = null;
				var partial = new List<BasePlayer>();
				foreach (BasePlayer player in BasePlayer.activePlayerList)
				{
					if (player.UserIDString == s || player.displayName.Equals(s, StringComparison.OrdinalIgnoreCase))
					{
						exact = player;
						break;
					}

					if (player.displayName.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
					{
						partial.Add(player);
					}
				}

				if (exact != null)
				{
					result.Add(exact);
				}
				else if (partial.Count == 1)
				{
					result.Add(partial[0]);
				}
				else
				{
					error = partial.Count == 0 ? Lang("TargetNotFound", null, s) : Lang("TargetAmbiguous", null, s);
					return null;
				}
			}

			result.RemoveAll(p => !CanListen(p));
			if (result.Count == 0)
			{
				error = Lang("NoListeners");
				return null;
			}

			return result;
		}

		#endregion

		// Carbon only routes Call()/CallHook()/Interface.Oxide.CallHook() to methods it has cached as hooks. Per
		// BaseHookable.BuildHookCache, that cache is built from every non-public instance method (any name) plus
		// public methods explicitly tagged [HookMethod] - a plain public method with no attribute is invisible to
		// it. Keeping these private (like every other hook in this file, e.g. OnPlayerDisconnected below) is the
		// same convention the rest of the plugin already follows, and needs no attribute.
		#region Public API

		/// <summary>
		/// Plays a converted voice file for one player: an NPC sitting on an invisible chair attached to that player.
		/// Returns the stream id, or -1 on failure (bad file name, player not connected/dead/sleeping, ...).
		///
		/// From another plugin:
		/// <code>
		/// int id = (int)(Interface.Oxide.CallHook("PlayVoiceFile", "siren", player) ?? -1);
		/// // or, with a typed reference (recommended - same call, but you get a compile error instead of a typo):
		/// [PluginReference] private Plugin OpusVoice;
		/// int id = OpusVoice?.Call&lt;int&gt;("PlayVoiceFile", "siren", player) ?? -1;
		/// </code>
		/// </summary>
		private int PlayVoiceFile(string file, BasePlayer target)
		{
			string error;
			VoiceStream stream = Play(file, new List<BasePlayer> { target }, out error);
			if (stream == null)
			{
				PrintWarning(error);
				return -1;
			}

			return stream.Id;
		}

		/// <summary>Plays a converted voice file for every connected player. Returns the stream id, or -1 on failure.</summary>
		private int PlayVoiceFileForAll(string file)
		{
			var targets = new List<BasePlayer>();
			foreach (BasePlayer player in BasePlayer.activePlayerList)
			{
				targets.Add(player);
			}

			string error;
			VoiceStream stream = Play(file, targets, out error);
			if (stream == null)
			{
				PrintWarning(error);
				return -1;
			}

			return stream.Id;
		}

		/// <summary>Stops a stream started with PlayVoiceFile/PlayVoiceFileForAll. Returns true if it was playing.</summary>
		private bool StopVoiceStream(int id) => StopStream(id);

		#endregion

		#region Streaming

		private sealed class VoiceFile
		{
			public string Name;
			public DateTime LastWriteUtc;
			public int SampleRate;
			public int FrameSamples;
			public List<byte[]> Frames;

			public double FrameDuration => (double)FrameSamples / SampleRate;

			public double Duration => Frames.Count * FrameDuration;
		}

		/// <summary>One listening player with their own chair + speaker NPC, both parented to that player.</summary>
		private sealed class Listener
		{
			public BasePlayer Player;
			public Connection Connection;
			public ClientEntity Chair;
			public ClientEntity Speaker;
			public ulong SteamId;
			public int NextFrame;
			public ushort Sequence;
		}

		private sealed class VoiceStream
		{
			public int Id;
			public VoiceFile File;
			public double T0;
			public List<Listener> Listeners = new List<Listener>();
		}

		private VoiceStream Play(string fileName, List<BasePlayer> targets, out string error)
		{
			error = null;

			VoiceFile file = LoadFile(fileName, out error);
			if (file == null)
			{
				return null;
			}

			// Audio starts right away: T0 is the moment the speakers are spawned.
			var stream = new VoiceStream { Id = _nextStreamId++, File = file, T0 = Now };
			foreach (BasePlayer target in targets)
			{
				if (!CanListen(target))
				{
					continue;
				}

				Listener listener = CreateListener(target);
				if (listener != null)
				{
					stream.Listeners.Add(listener);
				}
			}

			if (stream.Listeners.Count == 0)
			{
				error = Lang(targets.Count == 0 ? "NoListeners" : "SpawnFailed");
				return null;
			}

			_streams.Add(stream);
			EnsureTimer();
			return stream;
		}

		/// <summary>
		/// Spawns, only for <paramref name="player"/>, an invisible chair parented to the player and a player-prefab NPC
		/// mounted on that chair. Everything is client-side (Carbon ClientEntity); no server entities are created.
		/// </summary>
		private Listener CreateListener(BasePlayer player)
		{
			ulong steamId = SteamIdBase + _nextSpeakerId++;

			ProtoBuf.Entity chairProto = Facepunch.Pool.Get<ProtoBuf.Entity>();
			chairProto.parent = Facepunch.Pool.Get<ProtoBuf.ParentInfo>();
			chairProto.parent.uid = player.net.ID;
			chairProto.parent.bone = 0;
			chairProto.baseMountable = Facepunch.Pool.Get<ProtoBuf.BaseMountable>();

			ClientEntity chair = ClientEntity.Create(ChairPrefab, ChairOffset, Quaternion.identity, chairProto);
			if (chair == null)
			{
				chairProto.Dispose();
				return null;
			}

			ProtoBuf.Entity speakerProto = Facepunch.Pool.Get<ProtoBuf.Entity>();
			speakerProto.parent = Facepunch.Pool.Get<ProtoBuf.ParentInfo>();
			speakerProto.parent.uid = chair.NetID;
			speakerProto.parent.bone = 0;

			speakerProto.basePlayer = Facepunch.Pool.Get<ProtoBuf.BasePlayer>();
			speakerProto.basePlayer.userid = steamId;
			speakerProto.basePlayer.name = string.Empty;
			speakerProto.basePlayer.playerFlags = (int)BasePlayer.PlayerFlags.Connected;
			speakerProto.basePlayer.skinCol = -1f;
			speakerProto.basePlayer.skinTex = -1f;
			speakerProto.basePlayer.skinMesh = -1f;
			speakerProto.basePlayer.mounted = chair.NetID;
			speakerProto.basePlayer.modelState = Facepunch.Pool.Get<ModelState>();
			speakerProto.basePlayer.modelState.flags = (int)ModelState.Flag.Mounted;

			speakerProto.baseCombat = Facepunch.Pool.Get<ProtoBuf.BaseCombat>();
			speakerProto.baseCombat.state = (int)BaseCombatEntity.LifeState.Alive;
			speakerProto.baseCombat.health = 100f;
			speakerProto.baseCombat.maxHealth = 100f;

			ClientEntity speaker = ClientEntity.Create(SpeakerPrefab, Vector3.zero, Quaternion.identity, speakerProto);
			if (speaker == null)
			{
				speakerProto.Dispose();
				chair.Dispose();
				return null;
			}

			chair.Proto.baseMountable.mounted = speaker.NetID;

			// The chair must exist on the client before the NPC that is parented to it.
			chair.SpawnFor(player.Connection);
			speaker.SpawnFor(player.Connection);

			return new Listener
			{
				Player = player,
				Connection = player.Connection,
				Chair = chair,
				Speaker = speaker,
				SteamId = steamId,
			};
		}

		private void RemovePlayer(BasePlayer player)
		{
			if (player == null)
			{
				return;
			}

			foreach (VoiceStream stream in _streams)
			{
				for (int i = stream.Listeners.Count - 1; i >= 0; i--)
				{
					if (stream.Listeners[i].Player == player)
					{
						DestroyListener(stream.Listeners[i]);
						stream.Listeners.RemoveAt(i);
					}
				}
			}
		}

		private VoiceFile LoadFile(string name, out string error)
		{
			error = null;
			string cleanName = name ?? string.Empty;
			if (cleanName.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
			{
				cleanName = cleanName.Substring(0, cleanName.Length - FileExtension.Length);
			}

			// Only plain file names inside the data folder are allowed.
			if (cleanName.Length == 0 || cleanName != Path.GetFileName(cleanName) || cleanName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			{
				error = Lang("FileNotFound", null, name, _dataDir);
				return null;
			}

			string path = Path.Combine(_dataDir, cleanName + FileExtension);
			if (!File.Exists(path))
			{
				error = Lang("FileNotFound", null, cleanName, _dataDir);
				return null;
			}

			DateTime writeTime = File.GetLastWriteTimeUtc(path);
			VoiceFile cached;
			if (_cache.TryGetValue(cleanName, out cached) && cached.LastWriteUtc == writeTime)
			{
				return cached;
			}

			try
			{
				VoiceFile file = ReadVoiceFile(path);
				file.Name = cleanName;
				file.LastWriteUtc = writeTime;
				_cache[cleanName] = file;
				return file;
			}
			catch (Exception e) when (e is InvalidDataException || e is IOException || e is EndOfStreamException)
			{
				error = Lang("FileInvalid", null, cleanName, e.Message);
				return null;
			}
		}

		/// <summary>
		/// .rvoice layout (little endian): "RVOC", u16 version(1), u32 sampleRate, u16 frameSamples, u32 frameCount,
		/// then frameCount times { u16 length, byte[length] opusPacket }.
		/// </summary>
		private static VoiceFile ReadVoiceFile(string path)
		{
			using (var reader = new BinaryReader(File.OpenRead(path)))
			{
				if (reader.ReadUInt32() != 0x434F5652) // "RVOC"
				{
					throw new InvalidDataException("not an .rvoice file");
				}

				int version = reader.ReadUInt16();
				if (version != 1)
				{
					throw new InvalidDataException("unsupported version " + version);
				}

				int sampleRate = (int)reader.ReadUInt32();
				int frameSamples = reader.ReadUInt16();
				uint frameCount = reader.ReadUInt32();

				if (sampleRate < 8000 || sampleRate > 48000 || frameSamples == 0 || frameCount == 0 || frameCount > 3000000)
				{
					throw new InvalidDataException("invalid header");
				}

				var frames = new List<byte[]>((int)Math.Min(frameCount, 100000u));
				for (uint i = 0; i < frameCount; i++)
				{
					int length = reader.ReadUInt16();
					if (length == 0 || length > 1275)
					{
						throw new InvalidDataException("invalid frame length " + length);
					}

					byte[] frame = reader.ReadBytes(length);
					if (frame.Length != length)
					{
						throw new EndOfStreamException("file is truncated");
					}

					frames.Add(frame);
				}

				return new VoiceFile { SampleRate = sampleRate, FrameSamples = frameSamples, Frames = frames };
			}
		}

		private void EnsureTimer()
		{
			if (_tickTimer == null || _tickTimer.Destroyed)
			{
				_tickTimer = timer.Every(0.02f, Tick);
			}
		}

		private void Tick()
		{
			double now = Now;
			for (int i = _streams.Count - 1; i >= 0; i--)
			{
				VoiceStream stream = _streams[i];
				try
				{
					// Remove the speakers as soon as the audio has finished playing.
					if (now >= stream.T0 + stream.File.Duration + LeadSeconds)
					{
						FinishStream(stream);
						_streams.RemoveAt(i);
						continue;
					}

					SendDue(stream, now);
				}
				catch (Exception e)
				{
					PrintError("Stream #" + stream.Id + " failed: " + e);
					FinishStream(stream);
					_streams.RemoveAt(i);
				}
			}

			if (_streams.Count == 0 && _tickTimer != null)
			{
				_tickTimer.Destroy();
				_tickTimer = null;
			}
		}

		private void SendDue(VoiceStream stream, double now)
		{
			VoiceFile file = stream.File;
			int total = file.Frames.Count;
			int framesPerPacket = FramesPerPacket;

			// Frames that should have been delivered by now, including the lead buffered ahead of playback.
			int target = (int)Math.Floor((now - stream.T0 + LeadSeconds) / file.FrameDuration);
			if (target > total)
			{
				target = total;
			}

			foreach (Listener listener in stream.Listeners)
			{
				if (listener.Connection == null || !listener.Connection.connected)
				{
					continue;
				}

				while (listener.NextFrame < total && (listener.NextFrame + framesPerPacket <= target || target >= total))
				{
					int count = Math.Min(framesPerPacket, total - listener.NextFrame);
					int length = SteamVoicePacker.Pack(_packetBuffer, listener.SteamId, file.SampleRate, file.Frames, listener.NextFrame, count, listener.Sequence);
					SendVoice(listener.Connection, listener.Speaker.NetID, _packetBuffer, length);
					listener.NextFrame += count;
					listener.Sequence = unchecked((ushort)(listener.Sequence + count));
				}
			}
		}

		private static void SendVoice(Connection connection, NetworkableId speaker, byte[] buffer, int length)
		{
			using (NetWrite write = Net.sv.StartWrite())
			{
				write.PacketID(Message.Type.VoiceData);
				write.EntityID(speaker);
				// Same wire layout as BytesWithSize: a u32 length followed by the bytes.
				write.UInt32((uint)length);
				write.Write(buffer, 0, length);
				write.Send(new SendInfo(connection) { priority = Priority.Immediate });
			}
		}

		private bool StopStream(int id)
		{
			int index = _streams.FindIndex(s => s.Id == id);
			if (index < 0)
			{
				return false;
			}

			FinishStream(_streams[index]);
			_streams.RemoveAt(index);
			return true;
		}

		private void StopAll()
		{
			foreach (VoiceStream stream in _streams)
			{
				FinishStream(stream);
			}

			_streams.Clear();
			if (_tickTimer != null)
			{
				_tickTimer.Destroy();
				_tickTimer = null;
			}
		}

		private static void FinishStream(VoiceStream stream)
		{
			foreach (Listener listener in stream.Listeners)
			{
				DestroyListener(listener);
			}

			stream.Listeners.Clear();
		}

		/// <summary>Removes the NPC first, then the chair it sits on, and frees both entities.</summary>
		private static void DestroyListener(Listener listener)
		{
			try
			{
				listener.Speaker.KillFor(listener.Connection);
				listener.Chair.KillFor(listener.Connection);
			}
			catch (Exception)
			{
				// The connection may already be gone; the entities are disposed below regardless.
			}

			listener.Speaker.Dispose();
			listener.Chair.Dispose();
		}

		#endregion
	}

	/// <summary>
	/// Builds Steam voice payloads (the format SteamUser.GetVoice/DecompressVoice use, which Rust relays in VoiceData packets):
	/// <code>
	/// u64 steamId
	/// u8 0x0B, u16 sampleRate
	/// u8 0x06, u16 chunkLength, chunkLength bytes of { u16 opusLength, u16 sequence, opus[opusLength] }...
	/// u32 crc32   (IEEE, over everything before it)
	/// </code>
	/// All integers are little endian. This class deliberately has no game dependencies so it can be unit tested.
	/// </summary>
	public static class SteamVoicePacker
	{
		public const byte OpcodeSampleRate = 0x0B;
		public const byte OpcodeOpusPlc = 0x06;

		private const int MaxOpusFrame = 1275;
		private const int FixedOverhead = 8 + 3 + 3 + 4;
		private const int PerFrameOverhead = 4;

		private static readonly uint[] CrcTable = BuildCrcTable();

		public static int MaxPacketSize(int frameCount) => FixedOverhead + frameCount * (PerFrameOverhead + MaxOpusFrame);

		public static int Pack(byte[] destination, ulong steamId, int sampleRate, IList<byte[]> frames, int firstFrame, int frameCount, ushort firstSequence)
		{
			if (frameCount < 1 || firstFrame < 0 || firstFrame + frameCount > frames.Count)
			{
				throw new ArgumentOutOfRangeException(nameof(frameCount));
			}

			int position = 0;
			WriteUInt64(destination, ref position, steamId);

			destination[position++] = OpcodeSampleRate;
			WriteUInt16(destination, ref position, (ushort)sampleRate);

			destination[position++] = OpcodeOpusPlc;
			int chunkLengthPosition = position;
			position += 2;

			ushort sequence = firstSequence;
			for (int i = 0; i < frameCount; i++)
			{
				byte[] frame = frames[firstFrame + i];
				WriteUInt16(destination, ref position, (ushort)frame.Length);
				WriteUInt16(destination, ref position, sequence);
				Buffer.BlockCopy(frame, 0, destination, position, frame.Length);
				position += frame.Length;
				sequence = unchecked((ushort)(sequence + 1));
			}

			int chunkLength = position - chunkLengthPosition - 2;
			if (chunkLength > ushort.MaxValue)
			{
				throw new InvalidOperationException("voice chunk is too large");
			}

			int lengthCursor = chunkLengthPosition;
			WriteUInt16(destination, ref lengthCursor, (ushort)chunkLength);

			uint crc = Crc32(destination, position);
			WriteUInt32(destination, ref position, crc);
			return position;
		}

		public static uint Crc32(byte[] data, int length)
		{
			uint crc = 0xFFFFFFFFu;
			for (int i = 0; i < length; i++)
			{
				crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
			}

			return ~crc;
		}

		private static uint[] BuildCrcTable()
		{
			var table = new uint[256];
			for (uint i = 0; i < 256; i++)
			{
				uint c = i;
				for (int bit = 0; bit < 8; bit++)
				{
					c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
				}

				table[i] = c;
			}

			return table;
		}

		private static void WriteUInt16(byte[] buffer, ref int position, ushort value)
		{
			buffer[position++] = (byte)value;
			buffer[position++] = (byte)(value >> 8);
		}

		private static void WriteUInt32(byte[] buffer, ref int position, uint value)
		{
			for (int i = 0; i < 4; i++)
			{
				buffer[position++] = (byte)(value >> (8 * i));
			}
		}

		private static void WriteUInt64(byte[] buffer, ref int position, ulong value)
		{
			for (int i = 0; i < 8; i++)
			{
				buffer[position++] = (byte)(value >> (8 * i));
			}
		}
	}
}