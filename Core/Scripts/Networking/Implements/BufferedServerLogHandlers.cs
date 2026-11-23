using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace MultiplayerARPG
{
    [Flags]
    public enum ServerLogCategories
    {
        None = 0,
        Session = 1 << 0,
        Chat = 1 << 1,
        Attack = 1 << 2,
        Skill = 1 << 3,
        Reload = 1 << 4,
        Charge = 1 << 5,
        Buff = 1 << 6,
        Damage = 1 << 7,
        Craft = 1 << 8,
        Item = 1 << 9,
        Trade = 1 << 10,
        Reward = 1 << 11,
        Progress = 1 << 12,
        Quest = 1 << 13,
        Storage = 1 << 14,
        GMCommand = 1 << 15,
        Kill = 1 << 16,
        // Per-trigger / per-hit categories are noisy, so they are not in this set
        Default = Session | Chat | Craft | Item | Trade | Reward | Progress | Quest | Storage | GMCommand | Kill,
        All = ~0,
    }

    /// <summary>
    /// Generic fixed-size record. The main thread only copies already-existing references and primitives into it,
    /// all formatting happens on the writer thread. <see cref="Spec"/> names each slot per event.
    /// </summary>
    public struct ServerLogEvent
    {
        public long Ticks;
        public ServerLogCategories Category;
        public string Name;
        public string CharacterId;
        public string CharacterName;
        public string UserId;
        public string S1, S2, S3;
        public int I1, I2, I3, I4, I5;
        public float F1, F2;
        public bool B1;

        // Slot order: S1 S2 S3 I1 I2 I3 I4 I5 F1 F2 B1. null = slot unused. "name:Enum" prints the enum name.
        public static readonly Dictionary<string, string[]> Spec = new Dictionary<string, string[]>();
        private static void Sp(string evt, params string[] labels)
        {
            var arr = new string[11];
            Array.Copy(labels, arr, labels.Length);
            Spec[evt] = arr;
        }

        static ServerLogEvent()
        {
            Sp("LogEnterGame"); Sp("LogExitGame");
            Sp("LogEnterChat", "message", "toId", "toName", "channel:ChatChannel", "guildId", "mapChannel", null, null, null, null, "byServer");
            Sp("LogGMCommand", "message", "command", null, "userLevel", null, null, null, null, null, null, "allowed");
            Sp("LogAttackStart", "weapon", null, null, "seed", "fireSpread", null, null, null, null, null, "leftHand");
            Sp("LogAttackTrigger", null, null, null, "seed", "trigger");
            Sp("LogAttackTriggerFail", null, null, null, "seed", "trigger", "reason:ActionTriggerFailReasons");
            Sp("LogAttackInterrupt", null, null, null, "seed"); Sp("LogAttackEnd", null, null, null, "seed");
            Sp("LogUseSkillStart", "skill", "weapon", null, "seed", "skillLevel", "fireSpread", null, null, null, null, "leftHand");
            Sp("LogUseSkillTrigger", null, null, null, "seed", "trigger");
            Sp("LogUseSkillTriggerFail", null, null, null, "seed", "trigger", "reason:ActionTriggerFailReasons");
            Sp("LogUseSkillInterrupt", null, null, null, "seed"); Sp("LogUseSkillEnd", null, null, null, "seed");
            Sp("LogReloadStart"); Sp("LogReloadEnd"); Sp("LogReloadInterrupt");
            Sp("LogReloadTrigger", null, null, null, "trigger");
            Sp("LogReloadTriggerFail", null, null, null, "trigger", "reason:ActionTriggerFailReasons");
            Sp("LogChargeStart"); Sp("LogChargeEnd", null, null, null, null, null, null, null, null, null, null, "doAction");
            Sp("LogBuffApply", null, null, null, "buffDataId", "level");
            Sp("LogBuffRemove", null, null, null, "buffDataId", "level", "reason:BuffRemoveReasons");
            Sp("LogDamageReceived", "from", "skill", "weapon", "damage", "skillLevel", "hitBox:HitBoxPosition", "type:CombatAmountType", null, null, null, "overTime");
            Sp("LogKilled", "killer");
            Sp("LogCraftItem", "item", null, null, "amount", "gold");
            Sp("LogDismantleItems", "item", null, null, "level", "amount");
            Sp("LogRefine", "item", null, null, "level", "enhancers", "result(1=ok,2=destroyed)", null, null, "successBonus", null, "returning");
            Sp("LogRepair", "item", null, null, "level");
            Sp("LogEnhanceSocketItem", "item", "enhancer"); Sp("LogRemoveEnhancerFromItem", "item", "returned");
            Sp("LogItemDurabilityChanged", "item", null, null, null, null, null, null, null, "durability", "oldDurability", "destroyed");
            Sp("LogBuyVendingItem", "item", "sellerId", "sellerName", "amount", "price");
            Sp("LogSellVendingItem", "item", "buyerId", "buyerName", "amount", "price");
            Sp("LogExchangeDealingItemsAndGold", null, "withId", "withName", "gold", "itemCount");
            Sp("DealingItem", "item", "withId", "withName", "amount", "level");
            Sp("LogBuyNpcItem", "item", null, null, "level", "amount"); Sp("LogSellNpcItem", "item", null, null, "level", "amount");
            Sp("LogRewardItem", "item", null, null, "given:RewardGivenType", "amount", "level");
            Sp("LogRewardGold", null, null, null, "given:RewardGivenType", "gold");
            Sp("LogRewardExp", null, null, null, "given:RewardGivenType", "exp", null, null, null, null, null, "levelUp");
            Sp("LogRewardCurrency", "currency", null, null, "given:RewardGivenType", "amount", "currencyId");
            Sp("LogAddAttribute", "attribute", null, null, "added", "total"); Sp("LogResetAttributes");
            Sp("LogAddSkill", "skill", null, null, "added", "level"); Sp("LogResetSkills");
            Sp("LogQuestAccept", "quest"); Sp("LogQuestAbandon", "quest"); Sp("LogQuestComplete", "quest", null, null, "rewardIndex");
            Sp("LogMoveItemFromStorage", "storageOwner", null, null, "storage:StorageType", "storageIndex", "inventoryIndex", "amount", "message:UITextKeys", null, null, "success");
            Sp("LogMoveItemToStorage", "storageOwner", null, null, "storage:StorageType", "storageIndex", "inventoryIndex", "amount", "message:UITextKeys", null, null, "success");
            Sp("LogSwapOrMergeStorageItem", "storageOwner", null, null, "storage:StorageType", "from", "to", null, "message:UITextKeys", null, null, "success");
            Sp("LogDropped", null, null, null, "count");
        }

        public void AppendTo(StringBuilder sb)
        {
            AppendBody(sb);
            sb.Append('\n');
        }

        private void AppendBody(StringBuilder sb)
        {
            sb.Append(new DateTime(Ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(Name);
            if (!string.IsNullOrEmpty(CharacterId) || !string.IsNullOrEmpty(CharacterName))
            {
                sb.Append(" char=");
                Str(sb, CharacterName); sb.Append('(').Append(CharacterId).Append(')');
            }
            if (!string.IsNullOrEmpty(UserId)) sb.Append(" user=").Append(UserId);
            if (!Spec.TryGetValue(Name ?? string.Empty, out var l)) return;
            if (l[0] != null) Field(sb, l[0], S1);
            if (l[1] != null) Field(sb, l[1], S2);
            if (l[2] != null) Field(sb, l[2], S3);
            if (l[3] != null) Num(sb, l[3], I1);
            if (l[4] != null) Num(sb, l[4], I2);
            if (l[5] != null) Num(sb, l[5], I3);
            if (l[6] != null) Num(sb, l[6], I4);
            if (l[7] != null) Num(sb, l[7], I5);
            if (l[8] != null) sb.Append(' ').Append(l[8]).Append('=').Append(F1.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            if (l[9] != null) sb.Append(' ').Append(l[9]).Append('=').Append(F2.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            if (l[10] != null) sb.Append(' ').Append(l[10]).Append('=').Append(B1 ? "true" : "false");
        }

        private static void Field(StringBuilder sb, string label, string v)
        {
            if (string.IsNullOrEmpty(v)) return;
            sb.Append(' ').Append(label).Append('=');
            Str(sb, v);
        }

        private static void Str(StringBuilder sb, string v)
        {
            sb.Append('"');
            if (v != null)
                foreach (char c in v)
                    sb.Append(c == '"' ? '\'' : (c == '\n' || c == '\r') ? ' ' : c);
            sb.Append('"');
        }

        private static void Num(StringBuilder sb, string label, int v)
        {
            int i = label.IndexOf(':');
            if (i < 0) { sb.Append(' ').Append(label).Append('=').Append(v); return; }
            sb.Append(' ').Append(label, 0, i).Append('=');
            switch (label.Substring(i + 1))
            {
                case "ChatChannel": sb.Append((ChatChannel)v); break;
                case "ActionTriggerFailReasons": sb.Append((ActionTriggerFailReasons)v); break;
                case "BuffRemoveReasons": sb.Append((BuffRemoveReasons)v); break;
                case "HitBoxPosition": sb.Append((HitBoxPosition)v); break;
                case "CombatAmountType": sb.Append((CombatAmountType)v); break;
                case "RewardGivenType": sb.Append((RewardGivenType)v); break;
                case "StorageType": sb.Append((StorageType)v); break;
                case "UITextKeys": sb.Append((UITextKeys)v); break;
                default: sb.Append(v); break;
            }
        }
    }

    /// <summary>Called on the writer thread only. Never touch Unity APIs in here.</summary>
    public abstract class ServerLogSink : IDisposable
    {
        public abstract void Write(ServerLogEvent[] events, int count);
        public virtual void Dispose() { }
    }

    [Serializable]
    public struct ServerLogFileGroup
    {
        public string fileName;
        public ServerLogCategories categories;
    }

    /// <summary>
    /// Appends to Logs/{group}_{process name}_{yyyy-MM-dd}.audit.log, one file per category group and process.
    /// Not shared between processes: FileMode.Append is not atomic across processes, so sharing a file loses lines.
    /// Categories in no group go to "misc".
    /// </summary>
    public class FileServerLogSink : ServerLogSink
    {
        public const string FallbackFileName = "misc";
        private readonly Func<string> _prefixProvider;
        private readonly string[] _fileByBit; // bit index of ServerLogCategories -> file name, null = fallback
        private readonly Dictionary<string, StringBuilder> _sbs = new Dictionary<string, StringBuilder>();
        private readonly Dictionary<string, StreamWriter> _writers = new Dictionary<string, StreamWriter>();
        private string _currentSrc;
        private string _writerDate;

        public FileServerLogSink(Func<string> prefixProvider, string[] fileByBit)
        {
            _prefixProvider = prefixProvider;
            _fileByBit = fileByBit;
        }

        private string FileOf(ServerLogCategories cat)
        {
            for (int b = 0; b < _fileByBit.Length; ++b)
                if (((int)cat & (1 << b)) != 0 && !string.IsNullOrEmpty(_fileByBit[b]))
                    return _fileByBit[b];
            return FallbackFileName;
        }

        public override void Write(ServerLogEvent[] events, int count)
        {
            foreach (var sb in _sbs.Values)
                sb.Clear();
            string src = _prefixProvider();
            if (src != _currentSrc)
            {
                CloseWriters(); // prefix changed: release the old files
                _currentSrc = src;
            }
            for (int i = 0; i < count; ++i)
            {
                // File date comes from the event's own UTC timestamp, same clock as the line
                string key = FileOf(events[i].Category) + "|" + new DateTime(events[i].Ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd");
                if (!_sbs.TryGetValue(key, out var sb))
                    _sbs[key] = sb = new StringBuilder(16 * 1024);
                events[i].AppendTo(sb);
            }
            foreach (var kv in _sbs)
            {
                if (kv.Value.Length == 0)
                    continue;
                int sep = kv.Key.IndexOf('|');
                var writer = GetWriter(kv.Key.Substring(0, sep), src, kv.Key.Substring(sep + 1));
                writer.Write(kv.Value);
                writer.Flush(); // once per group per batch, not per line
            }
        }

        private StreamWriter GetWriter(string file, string src, string date)
        {
            string path = $"Logs/{file}_{src}_{date}.audit.log"; // keyed by path: src can change once the server instance is set up
            if (_writers.TryGetValue(path, out var writer))
                return writer;
            // Drop writers of other dates (day rollover) so old files are not kept open
            if (_writerDate != date)
            {
                CloseWriters();
                _writerDate = date;
            }
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            _writers[path] = writer;
            return writer;
        }

        private void CloseWriters()
        {
            foreach (var w in _writers.Values)
                w.Dispose();
            _writers.Clear();
        }

        public override void Dispose()
        {
            CloseWriters();
        }
    }

    /// <summary>
    /// Low-overhead <see cref="IServerLogHandlers"/>. Main thread: category check, then copy a few fields into a
    /// preallocated ring buffer (no allocation, no formatting, no I/O, never blocks). A background thread drains the
    /// ring every <see cref="flushIntervalMs"/> and hands batches to a <see cref="ServerLogSink"/>.
    /// All log hooks must be called from ONE thread (Unity main thread): the ring is single-producer.
    /// </summary>
    public class BufferedServerLogHandlers : MonoBehaviour, IServerLogHandlers
    {
        [SerializeField]
        private ServerLogCategories enabledCategories = ServerLogCategories.Default;
        [SerializeField, Tooltip("Categories grouped into one shared audit file each. Categories in no group go to 'misc'.")]
        private ServerLogFileGroup[] fileGroups =
        {
            new ServerLogFileGroup { fileName = "combat", categories = ServerLogCategories.Attack | ServerLogCategories.Skill | ServerLogCategories.Reload | ServerLogCategories.Charge | ServerLogCategories.Buff | ServerLogCategories.Damage | ServerLogCategories.Kill },
            new ServerLogFileGroup { fileName = "economy", categories = ServerLogCategories.Craft | ServerLogCategories.Item | ServerLogCategories.Trade | ServerLogCategories.Reward | ServerLogCategories.Storage },
            new ServerLogFileGroup { fileName = "social", categories = ServerLogCategories.Chat | ServerLogCategories.GMCommand },
            new ServerLogFileGroup { fileName = "session", categories = ServerLogCategories.Session | ServerLogCategories.Progress | ServerLogCategories.Quest },
        };
        [SerializeField, Tooltip("Must be a power of two. Events are dropped (and counted) when the ring is full.")]
        private int capacity = 16384;
        [SerializeField]
        private int flushIntervalMs = 250;

        private ServerLogEvent[] _ring;
        private int _mask;
        private int _head; // written by producer only
        private int _tail; // written by consumer only
        private int _dropped;
        private Thread _thread;
        private readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);
        private volatile bool _stop;
        private ServerLogSink _sink;
        private const int MaxCapacity = 1 << 24;
        private int _lostEvents;
        private bool _failureReported;

        public ServerLogCategories EnabledCategories { get { return enabledCategories; } set { enabledCategories = value; } }

        /// <summary>Override to use another sink (DB, HTTP...). Runs on the main thread once.</summary>
        protected virtual ServerLogSink CreateSink()
        {
            // Read-only after this point, so the writer thread can use it safely
            var fileByBit = new string[32];
            if (fileGroups != null)
                foreach (var g in fileGroups)
                    for (int b = 0; b < 32; ++b)
                        if (((int)g.categories & (1 << b)) != 0 && fileByBit[b] == null)
                            fileByBit[b] = g.fileName;
            return new FileServerLogSink(DefaultFilePrefix, fileByBit);
        }

        // ReferenceEquals: avoid UnityEngine.Object null check from the writer thread
        private static string DefaultFilePrefix()
        {
            var instance = MMO.MMOServerInstance.Singleton;
            string name = ReferenceEquals(instance, null) ? null : instance.LogFileName;
            return string.IsNullOrEmpty(name) ? "Server" : name;
        }

        protected virtual void OnValidate()
        {
            capacity = Mathf.Clamp(capacity, 2, MaxCapacity);
            flushIntervalMs = Mathf.Max(1, flushIntervalMs);
        }

        // Ring and thread are created on the first log call, so client builds that never log pay nothing
        private void EnsureStarted()
        {
            int size = 1;
            int cap = Mathf.Clamp(capacity, 2, MaxCapacity);
            while (size < cap) size <<= 1;
            _ring = new ServerLogEvent[size];
            _mask = size - 1;
            _sink = CreateSink();
            _thread = new Thread(WriterLoop) { IsBackground = true, Name = "ServerLogWriter" };
            _thread.Start();
        }

        protected virtual void OnApplicationQuit()
        {
            Shutdown();
        }

        protected virtual void OnDestroy()
        {
            Shutdown();
        }

        private void Shutdown()
        {
            if (_thread == null)
                return;
            _stop = true;
            _wake.Set();
            // The writer thread disposes the sink itself on exit, so a Join timeout cannot race with a write
            _thread.Join(3000);
            _thread = null;
        }

        private void WriterLoop()
        {
            try { WriterLoopInner(); }
            finally { _sink.Dispose(); }
        }

        private void WriterLoopInner()
        {
            var batch = new ServerLogEvent[1024];
            while (true)
            {
                bool stopping = _stop;
                int dropped = Interlocked.Exchange(ref _dropped, 0);
                int n;
                while ((n = Drain(batch)) > 0)
                {
                    try { _sink.Write(batch, n); }
                    catch (Exception ex) { OnWriteFailed(ex, n); }
                }
                dropped += _lostEvents;
                _lostEvents = 0;
                if (dropped > 0)
                {
                    batch[0] = new ServerLogEvent { Ticks = DateTime.UtcNow.Ticks, Name = "LogDropped", I1 = dropped };
                    try { _sink.Write(batch, 1); }
                    catch (Exception ex) { _lostEvents += dropped; OnWriteFailed(ex, 0); }
                }
                if (stopping)
                    return;
                _wake.Wait(Math.Max(1, flushIntervalMs));
            }
        }

        // Writer thread. Never kill the writer; count lost events (reported as LogDropped) and report only the first failure
        private void OnWriteFailed(Exception ex, int lost)
        {
            _lostEvents += lost;
            if (_failureReported)
                return;
            _failureReported = true;
            Debug.LogError("[ServerLog] Sink write failed, events are being lost: " + ex);
        }

        private int Drain(ServerLogEvent[] batch)
        {
            int tail = _tail;
            int head = Volatile.Read(ref _head);
            int n = Math.Min(head - tail, batch.Length);
            for (int i = 0; i < n; ++i)
                batch[i] = _ring[(tail + i) & _mask];
            Volatile.Write(ref _tail, tail + n);
            return n;
        }

        // I-slots follow ServerLogEvent.Spec order for the event name
        private void Add(ServerLogCategories cat, string name, IPlayerCharacterData c, string s1 = null, string s2 = null, string s3 = null, int i1 = 0, int i2 = 0, int i3 = 0, int i4 = 0, int i5 = 0, float f1 = 0f, float f2 = 0f, bool b1 = false)
        {
            if ((enabledCategories & cat) == 0)
                return;
            Push(cat, name, c?.Id, c?.CharacterName, c?.UserId, s1, s2, s3, i1, i2, i3, i4, i5, f1, f2, b1);
        }

        private void Push(ServerLogCategories cat, string name, string characterId, string characterName, string userId, string s1, string s2, string s3, int i1, int i2, int i3, int i4, int i5, float f1, float f2, bool b1)
        {
            if ((enabledCategories & cat) == 0)
                return;
            if (_ring == null)
                EnsureStarted();
            int head = _head;
            if (head - Volatile.Read(ref _tail) >= _ring.Length)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            _ring[head & _mask] = new ServerLogEvent
            {
                Ticks = DateTime.UtcNow.Ticks,
                Category = cat,
                Name = name,
                CharacterId = characterId,
                CharacterName = characterName,
                UserId = userId,
                S1 = s1, S2 = s2, S3 = s3,
                I1 = i1, I2 = i2, I3 = i3, I4 = i4, I5 = i5,
                F1 = f1, F2 = f2,
                B1 = b1,
            };
            Volatile.Write(ref _head, head + 1);
        }

        private bool On(ServerLogCategories cat) { return (enabledCategories & cat) != 0; }
        private static string IdOf(BaseGameData data) { return data ? data.Id : null; }
        private static string ItemKey(CharacterItem item) { var it = item.GetItem(); return it ? it.Id : null; }

        // Session
        public void LogEnterGame(IPlayerCharacterData c) { Add(ServerLogCategories.Session, nameof(LogEnterGame), c); }
        public void LogExitGame(string characterId, string userId) { Push(ServerLogCategories.Session, nameof(LogExitGame), characterId, null, userId, null, null, null, 0, 0, 0, 0, 0, 0f, 0f, false); }

        // Chat: sender is the character, receiver (whisper) in toId/toName, channel = Local/Global/Whisper/Party/Guild/System
        public void LogEnterChat(ChatMessage m)
        {
            Push(ServerLogCategories.Chat, nameof(LogEnterChat), m.senderId, m.senderName, m.senderUserId, m.message, m.receiverId, m.receiverName, (int)m.channel, m.guildId, m.channelId, 0, 0, 0f, 0f, m.sendByServer);
        }

        // GM command attempt (also unauthorized ones): allowed=false when the user level can't use it
        public void LogGMCommand(ChatMessage m, string command, int userLevel, bool allowed)
        {
            Push(ServerLogCategories.GMCommand, nameof(LogGMCommand), m.senderId, m.senderName, m.senderUserId, m.message, command, null, userLevel, 0, 0, 0, 0, 0f, 0f, allowed);
        }

        // Attack
        public void LogAttackStart(IPlayerCharacterData c, int seed, float[] triggerDurations, byte fireSpread, bool isLeftHand, CharacterItem weapon) { if (!On(ServerLogCategories.Attack)) return; Add(ServerLogCategories.Attack, nameof(LogAttackStart), c, ItemKey(weapon), null, null, seed, fireSpread, 0, 0, 0, 0f, 0f, isLeftHand); }
        public void LogAttackTrigger(IPlayerCharacterData c, int seed, byte triggerIndex) { Add(ServerLogCategories.Attack, nameof(LogAttackTrigger), c, i1: seed, i2: triggerIndex); }
        public void LogAttackTriggerFail(IPlayerCharacterData c, int seed, byte triggerIndex, ActionTriggerFailReasons reason) { Add(ServerLogCategories.Attack, nameof(LogAttackTriggerFail), c, i1: seed, i2: triggerIndex, i3: (int)reason); }
        public void LogAttackInterrupt(IPlayerCharacterData c, int seed) { Add(ServerLogCategories.Attack, nameof(LogAttackInterrupt), c, i1: seed); }
        public void LogAttackEnd(IPlayerCharacterData c, int seed) { Add(ServerLogCategories.Attack, nameof(LogAttackEnd), c, i1: seed); }

        // Skill
        public void LogUseSkillStart(IPlayerCharacterData c, int seed, float[] triggerDurations, byte fireSpread, bool isLeftHand, CharacterItem weapon, BaseSkill skill, int skillLevel) { if (!On(ServerLogCategories.Skill)) return; Add(ServerLogCategories.Skill, nameof(LogUseSkillStart), c, IdOf(skill), ItemKey(weapon), null, seed, skillLevel, fireSpread, 0, 0, 0f, 0f, isLeftHand); }
        public void LogUseSkillTrigger(IPlayerCharacterData c, int seed, byte triggerIndex) { Add(ServerLogCategories.Skill, nameof(LogUseSkillTrigger), c, i1: seed, i2: triggerIndex); }
        public void LogUseSkillTriggerFail(IPlayerCharacterData c, int seed, byte triggerIndex, ActionTriggerFailReasons reason) { Add(ServerLogCategories.Skill, nameof(LogUseSkillTriggerFail), c, i1: seed, i2: triggerIndex, i3: (int)reason); }
        public void LogUseSkillInterrupt(IPlayerCharacterData c, int seed) { Add(ServerLogCategories.Skill, nameof(LogUseSkillInterrupt), c, i1: seed); }
        public void LogUseSkillEnd(IPlayerCharacterData c, int seed) { Add(ServerLogCategories.Skill, nameof(LogUseSkillEnd), c, i1: seed); }

        // Reload
        public void LogReloadStart(IPlayerCharacterData c, float[] triggerDurations) { Add(ServerLogCategories.Reload, nameof(LogReloadStart), c); }
        public void LogReloadTrigger(IPlayerCharacterData c, byte triggerIndex) { Add(ServerLogCategories.Reload, nameof(LogReloadTrigger), c, i1: triggerIndex); }
        public void LogReloadTriggerFail(IPlayerCharacterData c, byte triggerIndex, ActionTriggerFailReasons reason) { Add(ServerLogCategories.Reload, nameof(LogReloadTriggerFail), c, i1: triggerIndex, i2: (int)reason); }
        public void LogReloadInterrupt(IPlayerCharacterData c) { Add(ServerLogCategories.Reload, nameof(LogReloadInterrupt), c); }
        public void LogReloadEnd(IPlayerCharacterData c) { Add(ServerLogCategories.Reload, nameof(LogReloadEnd), c); }

        // Charge
        public void LogChargeStart(IPlayerCharacterData c) { Add(ServerLogCategories.Charge, nameof(LogChargeStart), c); }
        public void LogChargeEnd(IPlayerCharacterData c, bool willDoActionWhenStopCharging) { Add(ServerLogCategories.Charge, nameof(LogChargeEnd), c, b1: willDoActionWhenStopCharging); }

        // Buff
        public void LogBuffApply(IPlayerCharacterData c, CharacterBuff b) { Add(ServerLogCategories.Buff, nameof(LogBuffApply), c, i1: b.dataId, i2: b.level); }
        public void LogBuffRemove(IPlayerCharacterData c, CharacterBuff b, BuffRemoveReasons reason) { Add(ServerLogCategories.Buff, nameof(LogBuffRemove), c, i1: b.dataId, i2: b.level, i3: (int)reason); }

        // Damage
        public void LogDamageReceived(IPlayerCharacterData c, HitBoxPosition position, Vector3 fromPosition, EntityInfo instigator, DamageElementMinMaxFloatAmounts damageAmounts, CombatAmountType combatAmountType, int totalDamage, CharacterItem weapon, BaseSkill skill, int skillLevel, CharacterBuff buff, bool isDamageOverTime)
        {
            if (!On(ServerLogCategories.Damage)) return; Add(ServerLogCategories.Damage, nameof(LogDamageReceived), c, instigator?.Id, IdOf(skill), ItemKey(weapon), totalDamage, skillLevel, (int)position, (int)combatAmountType, 0, 0f, 0f, isDamageOverTime);
        }
        public void LogKilled(IPlayerCharacterData c, EntityInfo lastAttacker) { Add(ServerLogCategories.Kill, nameof(LogKilled), c, lastAttacker?.Id); }

        // Craft / item
        public void LogCraftItem(IPlayerCharacterData c, ItemCraft itemCraft) { if (!On(ServerLogCategories.Craft)) return; Add(ServerLogCategories.Craft, nameof(LogCraftItem), c, IdOf(itemCraft.CraftingItem), null, null, itemCraft.Amount, itemCraft.RequireGold); }
        public void LogDismantleItems(IPlayerCharacterData c, IList<ItemAmount> dismantleItems)
        {
            if ((enabledCategories & ServerLogCategories.Craft) == 0) return;
            // list is only valid during the call: one record per item
            for (int i = 0; i < dismantleItems.Count; ++i)
                Add(ServerLogCategories.Craft, nameof(LogDismantleItems), c, IdOf(dismantleItems[i].item), null, null, dismantleItems[i].level, dismantleItems[i].amount);
        }
        public void LogRefine(IPlayerCharacterData c, CharacterItem refinedItem, IList<BaseItem> enhancerItems, float increaseSuccessRate, float decreaseRequireGoldRate, float chanceToNotDecreaseLevels, float chanceToNotDestroyItem, bool isSuccess, bool isDestroy, ItemRefineLevel itemRefineLevel, bool isReturning, ItemRefineFailReturning itemRefineFailReturning)
        {
            if (!On(ServerLogCategories.Item)) return; Add(ServerLogCategories.Item, nameof(LogRefine), c, ItemKey(refinedItem), null, null, refinedItem.level, enhancerItems != null ? enhancerItems.Count : 0, (isSuccess ? 1 : 0) | (isDestroy ? 2 : 0), 0, 0, increaseSuccessRate, 0f, isReturning);
        }
        public void LogRepair(IPlayerCharacterData c, CharacterItem repairedItem, ItemRepairPrice price) { if (!On(ServerLogCategories.Item)) return; Add(ServerLogCategories.Item, nameof(LogRepair), c, ItemKey(repairedItem), null, null, repairedItem.level); }
        public void LogEnhanceSocketItem(IPlayerCharacterData c, CharacterItem enhancedItem, BaseItem enhancerItem) { if (!On(ServerLogCategories.Item)) return; Add(ServerLogCategories.Item, nameof(LogEnhanceSocketItem), c, ItemKey(enhancedItem), IdOf(enhancerItem)); }
        public void LogRemoveEnhancerFromItem(IPlayerCharacterData c, CharacterItem enhancedItem, BaseItem returnEnhancerItem) { if (!On(ServerLogCategories.Item)) return; Add(ServerLogCategories.Item, nameof(LogRemoveEnhancerFromItem), c, ItemKey(enhancedItem), IdOf(returnEnhancerItem)); }
        public void LogItemDurabilityChanged(IPlayerCharacterData c, CharacterItem item, float oldDurability, float newDurability, bool destroyed) { if (!destroyed) return; if (!On(ServerLogCategories.Item)) return; Add(ServerLogCategories.Item, nameof(LogItemDurabilityChanged), c, ItemKey(item), null, null, 0, 0, 0, 0, 0, newDurability, oldDurability, destroyed); }

        // Trade
        public void LogBuyVendingItem(IPlayerCharacterData c, IPlayerCharacterData seller, CharacterItem buyItem, int price) { if (!On(ServerLogCategories.Trade)) return; Add(ServerLogCategories.Trade, nameof(LogBuyVendingItem), c, ItemKey(buyItem), seller?.Id, seller?.CharacterName, buyItem.amount, price); }
        public void LogSellVendingItem(IPlayerCharacterData c, IPlayerCharacterData buyer, CharacterItem buyItem, int price) { if (!On(ServerLogCategories.Trade)) return; Add(ServerLogCategories.Trade, nameof(LogSellVendingItem), c, ItemKey(buyItem), buyer?.Id, buyer?.CharacterName, buyItem.amount, price); }
        public void LogExchangeDealingItemsAndGold(IPlayerCharacterData c, IPlayerCharacterData dealing, int dealingGold, IList<CharacterItem> dealingItems)
        {
            if ((enabledCategories & ServerLogCategories.Trade) == 0) return;
            Add(ServerLogCategories.Trade, nameof(LogExchangeDealingItemsAndGold), c, null, dealing?.Id, dealing?.CharacterName, dealingGold, dealingItems != null ? dealingItems.Count : 0);
            if (dealingItems == null) return;
            for (int i = 0; i < dealingItems.Count; ++i)
                Add(ServerLogCategories.Trade, "DealingItem", c, ItemKey(dealingItems[i]), dealing?.Id, dealing?.CharacterName, dealingItems[i].amount, dealingItems[i].level);
        }
        public void LogBuyNpcItem(IPlayerCharacterData c, NpcSellItem sellItem, int amount) { if (!On(ServerLogCategories.Trade)) return; Add(ServerLogCategories.Trade, nameof(LogBuyNpcItem), c, IdOf(sellItem.item), null, null, sellItem.level, amount); }
        public void LogSellNpcItem(IPlayerCharacterData c, CharacterItem item, int amount) { if (!On(ServerLogCategories.Trade)) return; Add(ServerLogCategories.Trade, nameof(LogSellNpcItem), c, ItemKey(item), null, null, item.level, amount); }

        // Reward
        public void LogRewardItem(IPlayerCharacterData c, RewardGivenType t, BaseItem item, int amount) { if (!On(ServerLogCategories.Reward)) return; Add(ServerLogCategories.Reward, nameof(LogRewardItem), c, IdOf(item), null, null, (int)t, amount); }
        public void LogRewardItem(IPlayerCharacterData c, RewardGivenType t, CharacterItem item) { if (!On(ServerLogCategories.Reward)) return; Add(ServerLogCategories.Reward, nameof(LogRewardItem), c, ItemKey(item), null, null, (int)t, item.amount, item.level); }
        public void LogRewardGold(IPlayerCharacterData c, RewardGivenType t, int gold) { Add(ServerLogCategories.Reward, nameof(LogRewardGold), c, i1: (int)t, i2: gold); }
        public void LogRewardExp(IPlayerCharacterData c, RewardGivenType t, int exp, bool isLevelUp) { Add(ServerLogCategories.Reward, nameof(LogRewardExp), c, i1: (int)t, i2: exp, b1: isLevelUp); }
        public void LogRewardCurrency(IPlayerCharacterData c, RewardGivenType t, Currency currency, int amount) { if (!On(ServerLogCategories.Reward)) return; Add(ServerLogCategories.Reward, nameof(LogRewardCurrency), c, IdOf(currency), null, null, (int)t, amount, currency ? currency.DataId : 0); }
        public void LogRewardCurrency(IPlayerCharacterData c, RewardGivenType t, CharacterCurrency currency) { Add(ServerLogCategories.Reward, nameof(LogRewardCurrency), c, null, null, null, (int)t, currency.amount, currency.dataId); }

        // Progress
        public void LogAddAttribute(IPlayerCharacterData c, Attribute which, int howMuch, CharacterAttribute result) { if (!On(ServerLogCategories.Progress)) return; Add(ServerLogCategories.Progress, nameof(LogAddAttribute), c, IdOf(which), null, null, howMuch, result.amount); }
        public void LogResetAttributes(IPlayerCharacterData c) { Add(ServerLogCategories.Progress, nameof(LogResetAttributes), c); }
        public void LogAddSkill(IPlayerCharacterData c, BaseSkill which, int howMuch, CharacterSkill result) { if (!On(ServerLogCategories.Progress)) return; Add(ServerLogCategories.Progress, nameof(LogAddSkill), c, IdOf(which), null, null, howMuch, result.level); }
        public void LogResetSkills(IPlayerCharacterData c) { Add(ServerLogCategories.Progress, nameof(LogResetSkills), c); }

        // Quest
        public void LogQuestAccept(IPlayerCharacterData c, Quest quest) { if (!On(ServerLogCategories.Quest)) return; Add(ServerLogCategories.Quest, nameof(LogQuestAccept), c, IdOf(quest)); }
        public void LogQuestAbandon(IPlayerCharacterData c, Quest quest) { if (!On(ServerLogCategories.Quest)) return; Add(ServerLogCategories.Quest, nameof(LogQuestAbandon), c, IdOf(quest)); }
        public void LogQuestComplete(IPlayerCharacterData c, Quest quest, byte selectedRewardIndex) { if (!On(ServerLogCategories.Quest)) return; Add(ServerLogCategories.Quest, nameof(LogQuestComplete), c, IdOf(quest), null, null, selectedRewardIndex); }

        // Storage
        public void LogMoveItemFromStorage(IPlayerCharacterData c, StorageId storageId, bool storageIsLimitWeight, float storageWeightLimit, bool storageIsLimitSlot, int storageSlotLimit, IList<CharacterItem> storageItems, int storageItemIndex, int storageItemAmount, InventoryType inventoryType, int inventoryItemIndex, byte equipSlotIndexOrWeaponSet, bool success, UITextKeys gameMessage)
        {
            Add(ServerLogCategories.Storage, nameof(LogMoveItemFromStorage), c, storageId.storageOwnerId, null, null, (int)storageId.storageType, storageItemIndex, inventoryItemIndex, storageItemAmount, (int)gameMessage, 0f, 0f, success);
        }
        public void LogMoveItemToStorage(IPlayerCharacterData c, StorageId storageId, bool storageIsLimitWeight, float storageWeightLimit, bool storageIsLimitSlot, int storageSlotLimit, IList<CharacterItem> storageItems, int storageItemIndex, InventoryType inventoryType, int inventoryItemIndex, int inventoryItemAmount, byte equipSlotIndexOrWeaponSet, bool success, UITextKeys gameMessage)
        {
            Add(ServerLogCategories.Storage, nameof(LogMoveItemToStorage), c, storageId.storageOwnerId, null, null, (int)storageId.storageType, storageItemIndex, inventoryItemIndex, inventoryItemAmount, (int)gameMessage, 0f, 0f, success);
        }
        public void LogSwapOrMergeStorageItem(IPlayerCharacterData c, StorageId storageId, bool storageIsLimitSlot, int storageSlotLimit, IList<CharacterItem> storageItems, int fromIndex, int toIndex, bool success, UITextKeys gameMessage)
        {
            Add(ServerLogCategories.Storage, nameof(LogSwapOrMergeStorageItem), c, storageId.storageOwnerId, null, null, (int)storageId.storageType, fromIndex, toIndex, 0, (int)gameMessage, 0f, 0f, success);
        }
    }
}
