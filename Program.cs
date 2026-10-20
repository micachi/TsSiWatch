// TsSiWatch — EDCB 録画後実行用 SI 監視ツール (.NET Framework 4.8)
//
// 使い方:
//   TsSiWatch.exe <録画.m2ts> [--config <config.json>] [--dry-run] [--quiet]
//
// 動作:
//   1. 録画ファイルから NIT/SDT/TDT を解析 (ストリーミング、大容量対応)
//   2. ネットワーク種別 (110BS / 110スカパー! / 124/128スカパー! / DVB-C) を判定
//   3. data\<種別>.tsv にスナップショット (日付 + チャンネルリスト) を追記
//   4. 過去データ (最新スナップショット) と比較し、差分があれば Discord へ投下
//
// 設定: exe と同じフォルダの config.json (無ければ自動生成)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TsSiWatch
{
    // ------------------------------------------------------------------
    // 下位ユーティリティ
    // ------------------------------------------------------------------

    internal static class TsUtil
    {
        /// <summary>BCD (ニブル列) を整数へ。不正ニブルで打ち切り。</summary>
        public static long Bcd(long v)
        {
            long result = 0;
            for (int shift = 28; shift >= 0; shift -= 4)
            {
                long nib = (v >> shift) & 0xF;
                if (nib > 9) break;
                result = result * 10 + nib;
            }
            return result;
        }

        public static uint Crc32Mpeg(byte[] data, int len)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < len; i++)
            {
                crc ^= (uint)(data[i] << 24);
                for (int k = 0; k < 8; k++)
                    crc = ((crc & 0x80000000) != 0) ? (crc << 1) ^ 0x04C11DB7 : (crc << 1);
            }
            return crc;
        }

        public static string EscapeTsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('\\', ' ').Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        public static string JsonEscape(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------
    // PSI セクション再組立て
    // ------------------------------------------------------------------

    internal class PsiExtractor
    {
        private readonly Dictionary<int, List<byte>> _buf = new Dictionary<int, List<byte>>();
        public Action<int, byte[]> OnSection;
        public int CrcErrors;

        public void Feed(int pid, byte[] pkt, int payloadStart, bool pusi)
        {
            if (payloadStart >= 188) return;
            if (pusi)
            {
                int ptr = pkt[payloadStart];
                int chunkStart = payloadStart + 1;
                if (_buf.TryGetValue(pid, out var cur) && cur != null && cur.Count > 0)
                {
                    int avail = Math.Min(ptr, 188 - chunkStart);
                    for (int i = 0; i < avail; i++) cur.Add(pkt[chunkStart + i]);
                    Drain(pid);
                }
                var rest = new List<byte>();
                for (int i = chunkStart + ptr; i < 188; i++) rest.Add(pkt[i]);
                _buf[pid] = rest;
            }
            else
            {
                if (_buf.TryGetValue(pid, out var cur) && cur != null)
                {
                    for (int i = payloadStart; i < 188; i++) cur.Add(pkt[i]);
                }
            }
            Drain(pid);
        }

        private void Drain(int pid)
        {
            if (!_buf.TryGetValue(pid, out var buf) || buf == null) return;
            while (true)
            {
                if (buf.Count < 3) return;
                if (buf[0] == 0xFF) { _buf[pid] = null; return; }
                int secLen = ((buf[1] & 0x0F) << 8) | buf[2];
                int total = 3 + secLen;
                if (buf.Count < total) return;
                var section = buf.GetRange(0, total).ToArray();
                buf.RemoveRange(0, total);
                // TDT (PID 0x14, TID 0x70) は CRC を持たない (TOT 0x73 は CRC あり)
                bool noCrc = pid == 0x14 && section[0] == 0x70;
                bool ok = noCrc || (total >= 4 &&
                    TsUtil.Crc32Mpeg(section, total - 4) == GetUInt32(section, total - 4));
                if (ok) OnSection?.Invoke(pid, section);
                else CrcErrors++;
            }
        }

        private static uint GetUInt32(byte[] b, int off)
        {
            return ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];
        }
    }

    // ------------------------------------------------------------------
    // パケット列挙 (188 / 192 / 1508 対応, ストリーミング)
    // ------------------------------------------------------------------

    internal static class TsStream
    {
        public static void DetectLayout(string path, out int stride, out int off, out int nPkts)
        {
            stride = 188; off = 0; nPkts = 1;
            byte[] head;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int len = (int)Math.Min(4L * 1024 * 1024, fs.Length);
                head = new byte[len];
                int read = 0;
                while (read < len)
                {
                    int r = fs.Read(head, read, len - read);
                    if (r <= 0) break;
                    read += r;
                }
            }
            foreach (var cand in new[] { Tuple.Create(188, 1), Tuple.Create(192, 1), Tuple.Create(1508, 8) })
            {
                int st = cand.Item1, np = cand.Item2;
                foreach (int o in new[] { 0, 4 })
                {
                    int ok = 0, total = 0;
                    for (long i = 0; i + off < head.Length && i < (long)st * 200; i += st)
                    {
                        total++;
                        if (head[i + o] == 0x47) ok++;
                    }
                    if (total > 0 && ok / (double)total > 0.95) { stride = st; off = o; nPkts = np; return; }
                }
            }
        }

        /// <summary>ファイルから TS パケット (188 バイト) を逐次返す。</summary>
        public static IEnumerable<byte[]> IterPackets(string path, int stride, int off, int nPkts, Action<long, long> progress = null)
        {
            const int chunkPkts = 8192;
            long fileSize = new FileInfo(path).Length;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] leftover = new byte[0];
                int chunkBytes = stride * nPkts * chunkPkts;
                var buf = new byte[chunkBytes * 2];
                while (true)
                {
                    int r = fs.Read(buf, 0, chunkBytes);
                    if (r <= 0) break;
                    int dataLen = leftover.Length + r;
                    if (buf.Length < dataLen) Array.Resize(ref buf, dataLen);
                    if (leftover.Length > 0) Array.Copy(leftover, 0, buf, 0, leftover.Length);
                    int nblocks = dataLen / stride;
                    int usable = nblocks * stride;
                    for (int i = 0; i < nblocks; i++)
                    {
                        for (int k = 0; k < nPkts; k++)
                        {
                            int baseOff = i * stride + k * 188 + off;
                            if (baseOff + 188 > dataLen) continue;
                            if (buf[baseOff] != 0x47) continue;
                            var pkt = new byte[188];
                            Array.Copy(buf, baseOff, pkt, 0, 188);
                            yield return pkt;
                        }
                    }
                    progress?.Invoke(fs.Position, fileSize);
                    leftover = new byte[dataLen - usable];
                    Array.Copy(buf, usable, leftover, 0, dataLen - usable);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // データモデル
    // ------------------------------------------------------------------

    internal class SvcInfo
    {
        public string Name = "";
        public string Provider = "";
        public int Type;
    }

    internal class TpInfo
    {
        public int OnId;
        public string Delivery;      // satellite / cable
        public string System;
        public long FreqHz;
        public string Pol = "-";
        public long SymRate;
        public string Modulation = "";
        public string FecInner = "";
        public string FecOuter;
        public double? Orbital;
        public bool? East;
        public string RawHex;
        public Dictionary<int, SvcInfo> Services = new Dictionary<int, SvcInfo>();
    }

    internal class SiState
    {
        public string Label = "";
        public DateTime? TdtTime;
        public int PacketSize;
        public long Packets;
        public int CrcErrors;
        public Dictionary<int, string> NetworkNames = new Dictionary<int, string>();
        public Dictionary<int, int> Versions = new Dictionary<int, int>();
        // NIT actual (table_id 0x40) のトランスポートループに載っていた TP キー。
        // NIT other (0x41) は他ネットワークの「部分的な写し」で、これを同じ土俵で
        // 比較すると 110CS 録画が 110BS の履歴を壊すため、実データの判定に使う。
        public HashSet<int> ActualTpKeys = new HashSet<int>();
        public Dictionary<int, TpInfo> Tps = new Dictionary<int, TpInfo>();          // key: (onid<<16)|tsid
        public Dictionary<long, SvcInfo> Svcs = new Dictionary<long, SvcInfo>();     // key: (onid<<48)|(tsid<<16)|sid

        public static int TpKey(int onid, int tsid) { return (onid << 16) | tsid; }
        public static long SvcKey(int onid, int tsid, int sid) { return ((long)onid << 48) | ((long)tsid << 16) | (uint)sid; }

        public static string SanitizeLabel(string label)
        {
            var sb = new StringBuilder();
            foreach (char c in label)
                sb.Append("/\\:*?\"<>| ".IndexOf(c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------
    // テキストデコード (ARIB/EUC + ケーブル系ヘッドエンドの方言)
    // ------------------------------------------------------------------

    internal static class SiText
    {
        public static string AribText(byte[] b, int start, int len)
        {
            var outBytes = new List<byte>();
            int i = start, end = start + len;
            while (i < end)
            {
                byte c = b[i];
                if (c == 0x1B) { i += 3; continue; }
                if (c < 0x20 || c == 0x7F) { i++; continue; }
                outBytes.Add(c); i++;
            }
            return DecodeEuc(outBytes.ToArray());
        }

        private static string DecodeEuc(byte[] b)
        {
            if (b.Length == 0) return "";
            try { return Encoding.GetEncoding(51932).GetString(b).Trim(); } // euc-jis-2004
            catch { return Encoding.GetEncoding(51932).GetString(b).Trim(); }
        }

        private static string JisPair(int a, int b)
        {
            var bytes = new byte[] { 0x1B, 0x24, 0x42, (byte)a, (byte)b, 0x1B, 0x28, 0x42 };
            try { return Encoding.GetEncoding(50222).GetString(bytes); }   // iso-2022-jp (escape 対応)
            catch { return ""; }
        }

        /// <summary>
        /// SI 記述子テキストのデコード。
        /// 通常の ARIB/EUC のほか、ケーブル系ヘッドエンドの方言
        /// (既定 = JIS ペア / 0x0E = 半角 ASCII / 1B 7C = カタカナ下位バイト) を解釈する。
        /// </summary>
        public static string DecodeSiText(byte[] b, int start, int len)
        {
            if (len <= 0) return "";
            var seg = new byte[len];
            Array.Copy(b, start, seg, 0, len);
            b = seg;

            bool hasMarker = Array.IndexOf(b, (byte)0x0E) >= 0 || Array.IndexOf(b, (byte)0x0F) >= 0 || HasKanaMarker(b);
            if (!hasMarker)
            {
                // マーカーなしで全バイトが JIS ペア領域かつ偶数長 → JIS ペア列
                if (b.Length % 2 == 0 && AllInJisRange(b))
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < b.Length; i += 2) sb.Append(JisPair(b[i], b[i + 1]));
                    return sb.ToString().Trim();
                }
                return AribText(b, 0, b.Length);
            }

            var @out = new StringBuilder();
            int i2 = 0;
            const int PAIR = 0, ASCII = 1, KANA = 2;
            int mode = PAIR;
            while (i2 < b.Length)
            {
                int c = b[i2];
                if (c == 0x0E) { mode = ASCII; i2++; continue; }
                if (c == 0x0F) { mode = PAIR; i2++; continue; }
                if (c == 0x1B)
                {
                    if (i2 + 1 < b.Length && b[i2 + 1] == 0x7C) { mode = KANA; i2 += 2; continue; }
                    i2 += 3; continue;
                }
                if (mode == ASCII)
                {
                    if (c >= 0x20 && c <= 0x7E) @out.Append((char)c);
                    else if (c == 0xFE) @out.Append('・');
                    else if (c == 0xF9) @out.Append('ー');
                    else if (c == 0xAA) @out.Append('☆');
                    else if (c >= 0xA1 && c <= 0xFF)
                    {
                        var s = KanaLow(c);
                        if (s != null) @out.Append(s);
                    }
                    i2++; continue;
                }
                if (mode == KANA)
                {
                    if (c == 0xF9) @out.Append('ー');
                    else if (c == 0xFE) @out.Append('・');
                    else if (c >= 0x21 && c <= 0x7E)
                    {
                        if (i2 + 1 < b.Length && b[i2 + 1] >= 0x21 && b[i2 + 1] <= 0x7E)
                        {
                            @out.Append(JisPair(c, b[i2 + 1])); i2 += 2; continue;
                        }
                        @out.Append((char)c);
                    }
                    else if (c >= 0xA1 && c <= 0xFF)
                    {
                        var s = KanaLow(c);
                        if (s != null) @out.Append(s);
                    }
                    i2++; continue;
                }
                // PAIR モード
                if (c >= 0x21 && c <= 0x7E)
                {
                    if (i2 + 1 < b.Length && b[i2 + 1] >= 0x21 && b[i2 + 1] <= 0x7E)
                    {
                        @out.Append(JisPair(c, b[i2 + 1])); i2 += 2; continue;
                    }
                    @out.Append((char)c); i2++; continue;
                }
                if (c == 0xFE) { @out.Append('・'); i2++; continue; }
                if (c >= 0xA1 && c <= 0xFE)
                {
                    if (i2 + 1 < b.Length && b[i2 + 1] >= 0xA1)
                    {
                        try
                        {
                            var two = new byte[] { b[i2], b[i2 + 1] };
                            @out.Append(Encoding.GetEncoding(51932).GetString(two));
                            i2 += 2; continue;
                        }
                        catch { /* fallthrough */ }
                    }
                    if (c >= 0xA1 && c <= 0xDF) @out.Append((char)(0xFF61 + c - 0xA1));
                }
                i2++;
            }
            return @out.ToString().Trim();
        }

        private static bool HasKanaMarker(byte[] b)
        {
            for (int i = 0; i + 1 < b.Length; i++)
                if (b[i] == 0x1B && b[i + 1] == 0x7C) return true;
            return false;
        }

        private static bool AllInJisRange(byte[] b)
        {
            foreach (byte x in b) if (x < 0x21 || x > 0x7E) return false;
            return true;
        }

        private static string KanaLow(int c)
        {
            try { return Encoding.GetEncoding(51932).GetString(new byte[] { 0xA5, (byte)c }); }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------------
    // 記述子パース
    // ------------------------------------------------------------------

    internal static class Desc
    {
        public static IEnumerable<Tuple<int, int, int>> Parse(byte[] s, int start, int len)
        {
            int i = start, end = start + len;
            while (i + 2 <= end)
            {
                int tag = s[i];
                int dlen = s[i + 1];
                if (i + 2 + dlen > end) yield break;
                yield return Tuple.Create(tag, i + 2, dlen);
                i += 2 + dlen;
            }
        }
    }

    // ------------------------------------------------------------------
    // SI 収集
    // ------------------------------------------------------------------

    internal class Collector
    {
        public SiState State = new SiState();
        public int Sections, NitActual, NitOther, SdtActual, SdtOther;

        public void OnSection(int pid, byte[] s)
        {
            Sections++;
            if (pid == 0x14 && (s[0] == 0x70 || s[0] == 0x73)) { ParseTdt(s); return; }
            if (pid == 0x10 && (s[0] == 0x40 || s[0] == 0x41)) ParseNit(s, s[0] == 0x40);
            else if (pid == 0x11 && (s[0] == 0x42 || s[0] == 0x46)) ParseSdt(s);
        }

        // TDT (0x70) / TOT (0x73) 共通: s[3..8] に 40bit UTC 時刻
        private void ParseTdt(byte[] s)
        {
            if (s.Length < 8) return;
            long mjd = ((s[3] << 8) | s[4]) & 0xFFFF;
            long hh = TsUtil.Bcd(((long)s[5] << 16) | ((long)s[6] << 8) | s[7]) / 10000;
            long mm = TsUtil.Bcd(((long)s[5] << 16) | ((long)s[6] << 8) | s[7]) / 100 % 100;
            long ss = TsUtil.Bcd(((long)s[5] << 16) | ((long)s[6] << 8) | s[7]) % 100;
            // MJD → 日付 (EN 300 468 Annex C)
            long yp = (long)((mjd - 15078.2) / 365.25);
            long mp = (long)((mjd - 14956.1 - Math.Floor(yp * 365.25)) / 30.6001);
            long d = mjd - 14956 - (long)Math.Floor(yp * 365.25) - (long)Math.Floor(mp * 30.6001);
            long k = (mp == 14 || mp == 15) ? 1 : 0;
            long y = yp + k + 1900, m = mp - 1 - k * 12;
            if (m < 1 || m > 12 || d < 1 || d > 31) return;
            if (hh > 23 || mm > 59 || ss > 59) return;
            DateTime utc;
            try { utc = new DateTime((int)y, (int)m, (int)d, (int)hh, (int)mm, (int)ss, DateTimeKind.Utc); }
            catch { return; }
            State.TdtTime = utc.AddHours(9); // JST
        }

        private void ParseNit(byte[] s, bool actual)
        {
            if (actual) NitActual++; else NitOther++;
            if (s.Length < 14) return;
            int networkId = (s[3] << 8) | s[4];
            if (!State.Versions.ContainsKey(networkId)) State.Versions[networkId] = (s[5] >> 1) & 0x1F;
            int ndl = ((s[8] & 0x0F) << 8) | s[9];
            int p = 10 + ndl;
            if (p + 2 > s.Length - 4) return;
            // ネットワーク名 (0x40)
            foreach (var d in Desc.Parse(s, 10, ndl))
                if (d.Item1 == 0x40 && !State.NetworkNames.ContainsKey(networkId))
                    State.NetworkNames[networkId] = SiText.DecodeSiText(s, d.Item2, d.Item3);
            int tsl = ((s[p] & 0x0F) << 8) | s[p + 1];
            p += 2;
            int loopEnd = Math.Min(p + tsl, s.Length - 4);
            int i = p;
            while (i + 6 <= loopEnd)
            {
                int tsid = (s[i] << 8) | s[i + 1];
                int onid = (s[i + 2] << 8) | s[i + 3];
                int dl = ((s[i + 4] & 0x0F) << 8) | s[i + 5];
                int descStart = i + 6, descLen = dl;
                i += 6 + dl;
                var tp = GetTp(onid, tsid);
                if (actual && onid == networkId) State.ActualTpKeys.Add(SiState.TpKey(onid, tsid));
                foreach (var d in Desc.Parse(s, descStart, descLen))
                {
                    int tag = d.Item1, off = d.Item2, len = d.Item3;
                    if (tag == 0x43 && tp.FreqHz == 0) ParseSatellite(s, off, len, onid, tp);
                    else if (tag == 0x44 && tp.FreqHz == 0) ParseCable(s, off, len, tp);
                    else if (tag == 0x41)
                    {
                        for (int j = off; j + 3 <= off + len; j += 3)
                        {
                            int sid = (s[j] << 8) | s[j + 1];
                            GetSvc(onid, tsid, sid);
                        }
                    }
                }
            }
        }

        private TpInfo GetTp(int onid, int tsid)
        {
            int key = SiState.TpKey(onid, tsid);
            if (!State.Tps.TryGetValue(key, out var tp))
            {
                tp = new TpInfo { OnId = onid };
                State.Tps[key] = tp;
            }
            return tp;
        }

        private SvcInfo GetSvc(int onid, int tsid, int sid)
        {
            long key = SiState.SvcKey(onid, tsid, sid);
            if (!State.Svcs.TryGetValue(key, out var svc))
            {
                svc = new SvcInfo();
                State.Svcs[key] = svc;
            }
            return svc;
        }

        private void ParseSatellite(byte[] s, int off, int len, int onid, TpInfo tp)
        {
            if (len < 11) return;
            tp.Delivery = "satellite";
            tp.FreqHz = TsUtil.Bcd(GetInt(s, off, 4)) * 10000;
            tp.Orbital = TsUtil.Bcd(GetInt(s, off + 4, 2)) / 10.0;
            int b = s[off + 6];
            bool east = (b >> 7 & 1) == 1;
            tp.East = east;
            int pol = (b >> 5) & 0x3;
            tp.Pol = new[] { "H", "V", "L", "R" }[pol];
            int mod5 = b & 0x1F;
            long symfec = GetInt(s, off + 7, 4);
            tp.SymRate = TsUtil.Bcd(symfec >> 4) * 100;
            int fec = (int)(symfec & 0xF);
            bool isIsdb = mod5 > 3 || fec == 8 || fec == 9 || fec == 10 ||
                          onid == 4 || onid == 6 || onid == 7 || onid == 8;
            if (isIsdb)
            {
                tp.System = "ISDB-S";
                tp.Modulation = new Dictionary<int, string> { { 0, "auto" }, { 1, "QPSK" }, { 8, "TC8PSK" },
                    { 9, "2.6GHz音声" }, { 10, "アドバンスド狭帯域CS" } }.TryGetValue(mod5, out var m1) ? m1 : "unknown(" + mod5 + ")";
                var fecIsdb = new Dictionary<int, string> { { 0, "undefined" }, { 1, "1/2" }, { 2, "2/3" }, { 3, "3/4" },
                    { 4, "5/6" }, { 5, "7/8" }, { 8, "TC8PSK" }, { 15, "none" } };
                tp.FecInner = fecIsdb.TryGetValue(fec, out var f1) ? f1 : "unknown(" + fec + ")";
            }
            else
            {
                tp.System = ((b >> 1) & 1) == 1 ? "DVB-S2" : "DVB-S";
                tp.Modulation = new[] { "auto", "QPSK", "8PSK", "16QAM" }[b & 0x3];
                var fecDvb = new Dictionary<int, string> { { 0, "undefined" }, { 1, "1/2" }, { 2, "2/3" }, { 3, "3/4" },
                    { 4, "5/6" }, { 5, "7/8" }, { 6, "8/9" }, { 7, "3/5" }, { 8, "4/5" }, { 9, "9/10" }, { 15, "none" } };
                tp.FecInner = fecDvb.TryGetValue(fec, out var f2) ? f2 : "unknown(" + fec + ")";
            }
            tp.RawHex = Hex(s, off, len);
        }

        private void ParseCable(byte[] s, int off, int len, TpInfo tp)
        {
            if (len < 11) return;
            tp.Delivery = "cable";
            tp.System = "DVB-C";
            tp.FreqHz = TsUtil.Bcd(GetInt(s, off, 4)) * 100;
            tp.Pol = "-";
            tp.FecOuter = (s[off + 5] & 0xF) == 0 ? "not defined"
                : ((s[off + 5] & 0xF) == 1 || (s[off + 5] & 0xF) == 2) ? "RS(204/188)" : "code " + (s[off + 5] & 0xF) + " (reserved)";
            int mod = s[off + 6];
            tp.Modulation = new Dictionary<int, string> { { 0, "not defined" }, { 1, "16QAM" }, { 2, "32QAM" },
                { 3, "64QAM" }, { 4, "128QAM" }, { 5, "256QAM" } }.TryGetValue(mod, out var m) ? m : "code " + mod + " (reserved)";
            long symfec = GetInt(s, off + 7, 4);
            tp.SymRate = TsUtil.Bcd(symfec >> 4) * 100;
            int fec = (int)(symfec & 0xF);
            var fecDvb = new Dictionary<int, string> { { 0, "undefined" }, { 1, "1/2" }, { 2, "2/3" }, { 3, "3/4" },
                { 4, "5/6" }, { 5, "7/8" }, { 6, "8/9" }, { 15, "none" } };
            tp.FecInner = fecDvb.TryGetValue(fec, out var f) ? f : "unknown(" + fec + ")";
            tp.RawHex = Hex(s, off, len);
        }

        private void ParseSdt(byte[] s)
        {
            if (s[0] == 0x42) SdtActual++; else SdtOther++;
            if (s.Length < 17) return;
            int onid = (s[8] << 8) | s[9];
            int end = s.Length - 4;
            int p = 13, endLoop;
            // 標準形: onid(2)+予約(1)+ループ長(2) → 13 から
            int dl0 = ((s[11] & 0x0F) << 8) | s[12];
            if (13 + dl0 + 4 == s.Length) endLoop = 13 + dl0;
            else { p = 11; endLoop = end; } // 非標準形 (ループ長なし)
            while (p + 5 <= endLoop)
            {
                int sid = (s[p] << 8) | s[p + 1];
                int dl = ((s[p + 3] & 0x0F) << 8) | s[p + 4];
                int descStart = p + 5, descLen = dl;
                p += 5 + dl;
                if (descStart + descLen > s.Length) break;
                int tsid = (s[3] << 8) | s[4];
                foreach (var d in Desc.Parse(s, descStart, descLen))
                {
                    if (d.Item1 != 0x48) continue; // service_descriptor
                    int off = d.Item2, len = d.Item3;
                    if (len < 2) continue;
                    int stype = s[off];
                    int plen = s[off + 1];
                    int pp = off + 2;
                    string provider = SiText.DecodeSiText(s, pp, Math.Min(plen, off + len - pp));
                    pp += plen;
                    string name = pp < off + len && s[pp] <= off + len - pp - 1
                        ? SiText.DecodeSiText(s, pp + 1, Math.Min(s[pp], off + len - pp - 1)) : "";
                    var svc = GetSvc(onid, tsid, sid);
                    svc.Type = stype;
                    svc.Provider = provider;
                    svc.Name = name;
                }
            }
        }

        private static long GetInt(byte[] s, int off, int len)
        {
            long v = 0;
            for (int i = 0; i < len; i++) v = (v << 8) | s[off + i];
            return v;
        }

        private static string Hex(byte[] s, int off, int len)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < len; i++) sb.Append(s[off + i].ToString("x2"));
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------
    // スキャン
    // ------------------------------------------------------------------

    internal static class Scanner
    {
        public static SiState Scan(string path)
        {
            TsStream.DetectLayout(path, out int stride, out int off, out int nPkts);
            var extractor = new PsiExtractor();
            var collector = new Collector();
            extractor.OnSection = collector.OnSection;

            long packets = 0, lastReport = 0;
            long total = new FileInfo(path).Length;
            foreach (var pkt in TsStream.IterPackets(path, stride, off, nPkts,
                (pos, size) =>
                {
                    if (pos - lastReport > 512L * 1024 * 1024)
                    {
                        lastReport = pos;
                        Console.Error.WriteLine("  ... {0} MB 走査済", pos / (1024 * 1024));
                    }
                }))
            {
                packets++;
                int pid = ((pkt[1] & 0x1F) << 8) | pkt[2];
                if (pid != 0x10 && pid != 0x11 && pid != 0x14) continue;
                bool pusi = (pkt[1] & 0x40) != 0;
                int afc = (pkt[3] >> 4) & 0x3;
                int p = 4;
                if ((afc & 0x2) != 0) p += 1 + pkt[4];
                if ((afc & 0x1) != 0 && p < 188)
                    extractor.Feed(pid, pkt, p, pusi);
            }

            collector.State.PacketSize = stride;
            collector.State.Packets = packets;
            collector.State.CrcErrors = extractor.CrcErrors;
            return collector.State;
        }
    }

    // ------------------------------------------------------------------
    // 種別判定
    // ------------------------------------------------------------------

    internal class NetGroup
    {
        public int OnId;
        public string Label = "";
        public SiState State;
    }

    internal static class Classifier
    {
        /// <summary>ONID と伝送路から分類ラベルを決める。cable が最優先。</summary>
        public static string LabelFor(int onid, bool cable)
        {
            if (cable) return "DVB-C";
            if (onid == 4) return "110BS";
            if (onid == 6 || onid == 7) return "110";
            return "124/128";
        }

        /// <summary>
        /// 1 本の録画をネットワーク (ONID) ごとに分割する。
        ///
        /// 1 本の TS には NIT actual (0x40) と NIT other (0x41) の両方が入る。
        /// NIT other は他ネットワークの「部分的な写し」で、たとえば 110CS の録画にも
        /// ONID 4 の TP が 1 本だけ載る。これをそのまま混ぜると
        ///   - 110CS 録画が「ONID 4 を含む」ため 110BS と判定され
        ///   - 110BS.tsv が CS の内容で上書きされ、BS の履歴が吹っ飛ぶ
        /// という事故になるので、実データは NIT actual 由来の TP だけを採用する。
        /// (NIT actual が 1 本も無いストリームでは従来どおり全 TP を使う)
        /// </summary>
        public static List<NetGroup> Split(SiState state)
        {
            var groups = new List<NetGroup>();
            bool actualOnly = state.ActualTpKeys.Count > 0;

            var onids = new List<int>();
            foreach (var tp in state.Tps.Values)
                if (!onids.Contains(tp.OnId)) onids.Add(tp.OnId);
            onids.Sort();

            foreach (int onid in onids)
            {
                var g = new SiState
                {
                    TdtTime = state.TdtTime,
                    PacketSize = state.PacketSize,
                    Packets = state.Packets,
                    CrcErrors = state.CrcErrors
                };
                foreach (var kv in state.Tps)
                {
                    if (kv.Value.OnId != onid) continue;
                    if (actualOnly && !state.ActualTpKeys.Contains(kv.Key)) continue;
                    g.Tps[kv.Key] = kv.Value;
                }
                if (g.Tps.Count == 0) continue;

                // サービスは残した TP に属するものだけを採用
                foreach (var kv in state.Svcs)
                {
                    int sonid = (int)(kv.Key >> 48);
                    if (sonid != onid) continue;
                    int stsid = (int)((kv.Key >> 16) & 0xFFFF);
                    if (!g.Tps.ContainsKey(SiState.TpKey(sonid, stsid))) continue;
                    g.Svcs[kv.Key] = kv.Value;
                }

                bool cable = false;
                foreach (var tp in g.Tps.Values) if (tp.Delivery == "cable") cable = true;
                g.Label = LabelFor(onid, cable);
                if (state.NetworkNames.TryGetValue(onid, out var nm)) g.NetworkNames[onid] = nm;
                groups.Add(new NetGroup { OnId = onid, Label = g.Label, State = g });
            }
            return groups;
        }
    }

    // ------------------------------------------------------------------
    // 差分
    // ------------------------------------------------------------------

    internal class DiffResult
    {
        public List<string> TpAdded = new List<string>();
        public List<string> TpRemoved = new List<string>();
        public List<string> TpChanged = new List<string>();
        public List<string> SvcAdded = new List<string>();
        public List<string> SvcRemoved = new List<string>();
        public List<string> SvcMoved = new List<string>();
        public List<string> SvcChanged = new List<string>();

        public bool Any()
        {
            return TpAdded.Count + TpRemoved.Count + TpChanged.Count +
                   SvcAdded.Count + SvcRemoved.Count + SvcMoved.Count + SvcChanged.Count > 0;
        }
        public int Total() { return TpAdded.Count + TpRemoved.Count + TpChanged.Count +
            SvcAdded.Count + SvcRemoved.Count + SvcMoved.Count + SvcChanged.Count; }
    }

    internal static class Differ
    {
        private static readonly string[] TpFields = { "freq", "pol", "symrate", "mod", "fec", "fec_outer", "system" };

        public static DiffResult Diff(SiState oldState, SiState newState)
        {
            var d = new DiffResult();
            var oldTps = new Dictionary<int, string>();
            foreach (var kv in oldState.Tps) oldTps[kv.Key] = TpSig(kv.Value);
            var newTps = new Dictionary<int, string>();
            foreach (var kv in newState.Tps) newTps[kv.Key] = TpSig(kv.Value);

            foreach (var kv in newTps)
                if (!oldTps.ContainsKey(kv.Key)) d.TpAdded.Add(FormatTp(newState, kv.Key));
            foreach (var kv in oldTps)
                if (!newTps.ContainsKey(kv.Key)) d.TpRemoved.Add(FormatTp(oldState, kv.Key));
            foreach (var kv in newTps)
            {
                if (!oldTps.TryGetValue(kv.Key, out var oldSig)) continue;
                var oldParts = oldSig.Split('\t');
                var newParts = kv.Value.Split('\t');
                for (int i = 0; i < TpFields.Length && i < oldParts.Length && i < newParts.Length; i++)
                {
                    if (oldParts[i] == newParts[i]) continue;
                    string val = oldParts[i] + " → " + newParts[i];
                    d.TpChanged.Add(FormatTpId(newState, kv.Key) + " " + TpFields[i] + ": " + val);
                }
            }

            var oldSvcs = new Dictionary<long, string>();
            foreach (var kv in oldState.Svcs) oldSvcs[kv.Key] = SvcSig(kv.Value);
            var newSvcs = new Dictionary<long, string>();
            foreach (var kv in newState.Svcs) newSvcs[kv.Key] = SvcSig(kv.Value);

            var moved = new HashSet<long>();
            foreach (var kv in newSvcs)
            {
                long onid = kv.Key >> 48, sid = kv.Key & 0xFFFF;
                long tsid = (kv.Key >> 16) & 0xFFFFFFFF;
                // 同一 (ONID, SID) が旧側にあって TSID が違う → 移動
                foreach (var okv in oldSvcs)
                {
                    if ((okv.Key >> 48) != onid || (okv.Key & 0xFFFF) != sid) continue;
                    long oldTsid = (okv.Key >> 16) & 0xFFFFFFFF;
                    if (oldTsid != tsid) { moved.Add(kv.Key); d.SvcMoved.Add("SID " + sid + " " + SvcName(okv.Value) + ": TSID " + oldTsid + " → " + tsid); }
                    break;
                }
            }
            foreach (var kv in newSvcs)
            {
                if (moved.Contains(kv.Key)) continue;
                if (!oldSvcs.ContainsKey(kv.Key)) d.SvcAdded.Add(FormatSvc(newState, kv.Key));
            }
            var removedMoved = new HashSet<long>();
            foreach (var kv in oldSvcs)
            {
                long onid = kv.Key >> 48, sid = kv.Key & 0xFFFF;
                long tsid = (kv.Key >> 16) & 0xFFFFFFFF;
                foreach (var nkv in newSvcs)
                {
                    if ((nkv.Key >> 48) != onid || (nkv.Key & 0xFFFF) != sid) continue;
                    long newTsid = (nkv.Key >> 16) & 0xFFFFFFFF;
                    if (newTsid != tsid) { removedMoved.Add(kv.Key); break; }
                }
            }
            foreach (var kv in oldSvcs)
            {
                if (removedMoved.Contains(kv.Key)) continue;
                if (!newSvcs.ContainsKey(kv.Key)) d.SvcRemoved.Add(FormatSvc(oldState, kv.Key));
            }
            foreach (var kv in newSvcs)
            {
                if (!oldSvcs.TryGetValue(kv.Key, out var oldSig)) continue;
                var oldParts = oldSig.Split('\t');
                var newParts = kv.Value.Split('\t');
                var chg = new List<string>();
                if (oldParts[0] != newParts[0]) chg.Add("name " + oldParts[0] + " → " + newParts[0]);
                if (oldParts[1] != newParts[1]) chg.Add("provider " + oldParts[1] + " → " + newParts[1]);
                if (oldParts[2] != newParts[2]) chg.Add("type " + oldParts[2] + " → " + newParts[2]);
                if (chg.Count > 0)
                    d.SvcChanged.Add(FormatSvcId(newState, kv.Key) + ": " + string.Join(", ", chg));
            }
            return d;
        }

        private static string TpSig(TpInfo tp)
        {
            return string.Join("\t", tp.FreqHz, tp.Pol, tp.SymRate, tp.Modulation, tp.FecInner, tp.FecOuter, tp.System);
        }

        /// <summary>
        /// 新しい状態でサービス名/提供者名が取れなかったとき、前回スナップショットの
        /// 値で埋める。短い録画や他 TP の SDT が揃わない録画で名前が空になり、
        /// 「name 〇〇 → (空)」という実体のない変更として通知されるのを防ぐ。
        /// </summary>
        public static void CarryOverNames(SiState oldState, SiState newState)
        {
            if (oldState == null) return;
            foreach (var kv in newState.Svcs)
            {
                if (!oldState.Svcs.TryGetValue(kv.Key, out var oldSvc)) continue;
                var s = kv.Value;
                if (string.IsNullOrWhiteSpace(s.Name)) s.Name = oldSvc.Name;
                if (string.IsNullOrWhiteSpace(s.Provider)) s.Provider = oldSvc.Provider;
            }
        }
        private static string SvcSig(SvcInfo s) { return string.Join("\t", s.Name, s.Provider, s.Type); }
        private static string SvcName(string sig) { return sig.Split('\t')[0]; }

        private static string FormatTpId(SiState st, int key)
        {
            int onid = key >> 16, tsid = key & 0xFFFF;
            return "ONID " + onid + " TSID " + tsid;
        }
        private static string FormatTp(SiState st, int key) { return FormatTpId(st, key); }
        private static string FormatSvcId(SiState st, long key)
        {
            long onid = key >> 48, sid = key & 0xFFFF, tsid = (key >> 16) & 0xFFFFFFFF;
            return "ONID " + onid + " TSID " + tsid + " SID " + sid;
        }
        private static string FormatSvc(SiState st, long key)
        {
            return FormatSvcId(st, key) + " " + SvcName(oldOrNew(st, key));
        }
        private static string oldOrNew(SiState st, long key) { return st.Svcs.TryGetValue(key, out var s) ? SvcSig(s) : ""; }
    }

    // ------------------------------------------------------------------
    // TSV スナップショット保存
    // ------------------------------------------------------------------

    internal static class TsvStore
    {
        public static readonly string[] Header =
        {
            "date", "label", "onid", "tsid", "freq_hz", "pol", "symrate", "modulation",
            "fec_inner", "fec_outer", "system", "service_id", "service_name", "provider", "service_type"
        };

        public static List<string[]> LoadRows(string path)
        {
            var rows = new List<string[]>();
            if (!File.Exists(path)) return rows;
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line.StartsWith("date\t") || line.StartsWith("date\t".TrimEnd('\t'))) continue;
                var parts = line.Split('\t');
                if (parts.Length < Header.Length) continue;
                rows.Add(parts);
            }
            return rows;
        }

        public static DateTime LatestDate(List<string[]> rows)
        {
            DateTime latest = DateTime.MinValue;
            foreach (var r in rows)
            {
                if (DateTime.TryParseExact(r[0], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dt) && dt > latest) latest = dt;
            }
            return latest;
        }

        public static SiState BuildState(List<string[]> rows)
        {
            var st = new SiState();
            foreach (var r in rows)
            {
                int onid = int.Parse(r[2]);
                int tsid = int.Parse(r[3]);
                long freq = long.Parse(r[4]);
                string pol = r[5];
                long sym = long.Parse(r[6]);
                string mod = r[7], fec = r[8], fecOuter = r[9], sys = r[10];
                int sid = int.Parse(r[11]);
                string name = r[12], provider = r[13];
                int stype = int.Parse(r[14]);

                var tp = st.Tps.TryGetValue(SiState.TpKey(onid, tsid), out var t)
                    ? t : (st.Tps[SiState.TpKey(onid, tsid)] = new TpInfo { OnId = onid });
                tp.Delivery = sys == "DVB-C" ? "cable" : "satellite";
                tp.System = sys; tp.FreqHz = freq; tp.Pol = pol; tp.SymRate = sym;
                tp.Modulation = mod; tp.FecInner = fec; tp.FecOuter = fecOuter;
                st.Svcs[SiState.SvcKey(onid, tsid, sid)] = new SvcInfo { Name = name, Provider = provider, Type = stype };
                // サービスが 1 つも無い TP は SID 0 のプレースホルダ行で保存される。
                // これをサービスとして数えると毎回「SID 0 削除」が出るので無視する。
                if (sid == 0 && name.Length == 0) st.Svcs.Remove(SiState.SvcKey(onid, tsid, sid));
            }
            return st;
        }

        /// <summary>
        /// 旧バージョンが 1 ファイルに混在させたネットワークを整理する (起動時に毎回実行、冪等)。
        ///
        ///  - 同じ日付のスナップショットに複数 ONID が入っていたら、サービス数が最多の
        ///    ONID (= 実際に受信していたネットワーク) の行だけを残す。他の ONID は
        ///    NIT other 由来の部分情報で、残すと次回の比較基準を壊す (110CS 録画の
        ///    ONID 4 は TP 1 本しか載らない)。
        ///  - 残した行のラベルを ONID から計算し直し、該当ファイルへ移す。
        ///  - 変更するファイルは &lt;name&gt;.&lt;yyyyMMdd-HHmmss&gt;.bak に退避する。
        /// </summary>
        public static List<string> Migrate(string dataDir)
        {
            var report = new List<string>();
            var files = Directory.GetFiles(dataDir, "*.tsv");
            if (files.Length == 0) return report;

            var final = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
            var original = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dropCount = new Dictionary<string, int>();
            var moveCount = new Dictionary<string, int>();

            foreach (var f in files)
            {
                var rows = LoadRows(f);
                if (rows.Count == 0) continue;
                original[f] = string.Join("\n", rows.Select(r => string.Join("\t", r)));
                final[f] = new List<string[]>();   // 全行が移る場合に備えて空で登録

                var byDate = new Dictionary<string, List<string[]>>();
                foreach (var r in rows)
                {
                    if (!byDate.TryGetValue(r[0], out var list)) byDate[r[0]] = list = new List<string[]>();
                    list.Add(r);
                }

                foreach (var snap in byDate.Values)
                {
                    var svcCount = new Dictionary<int, HashSet<string>>();
                    var tpCount = new Dictionary<int, HashSet<int>>();
                    foreach (var r in snap)
                    {
                        int onid = int.Parse(r[2]);
                        if (!svcCount.TryGetValue(onid, out var ss))
                        {
                            svcCount[onid] = ss = new HashSet<string>();
                            tpCount[onid] = new HashSet<int>();
                        }
                        ss.Add(r[11]);
                        tpCount[onid].Add(int.Parse(r[3]));
                    }
                    int primary = svcCount.OrderByDescending(k => k.Value.Count)
                                          .ThenByDescending(k => tpCount[k.Key].Count)
                                          .ThenBy(k => k.Key).First().Key;

                    // 「旧版がラベルを取り違えて 1 ファイルに混ぜた」スナップショットだけを
                    // 部分情報の破棄対象にする。新版が正しく書いた複数 ONID のスナップショット
                    // (ラベルが ONID と一致) は触らない。
                    bool legacyMixed = false;
                    foreach (var r in snap)
                    {
                        int o = int.Parse(r[2]);
                        if (r[1] != Classifier.LabelFor(o, r[10] == "DVB-C")) { legacyMixed = true; break; }
                    }

                    foreach (var r in snap)
                    {
                        int onid = int.Parse(r[2]);
                        if (legacyMixed && svcCount.Count > 1 && onid != primary)
                        {
                            string dk = Path.GetFileName(f) + "|" + r[0] + "|" + onid;
                            dropCount.TryGetValue(dk, out int dc);
                            dropCount[dk] = dc + 1;
                            continue;
                        }
                        var copy = (string[])r.Clone();
                        copy[1] = Classifier.LabelFor(onid, r[10] == "DVB-C");
                        string target = Path.Combine(dataDir, SiState.SanitizeLabel(copy[1]) + ".tsv");
                        if (!final.TryGetValue(target, out var tl)) final[target] = tl = new List<string[]>();
                        tl.Add(copy);
                        if (!string.Equals(target, f, StringComparison.OrdinalIgnoreCase))
                        {
                            string mk = Path.GetFileName(f) + " → " + Path.GetFileName(target);
                            moveCount.TryGetValue(mk, out int mc);
                            moveCount[mk] = mc + 1;
                        }
                    }
                }
            }

            foreach (var kv in dropCount)
            {
                var p = kv.Key.Split('|');
                report.Add(string.Format("{0} {1} の ONID {2} 行 {3} 件を破棄 (他ネットワークの部分情報)",
                    p[0], p[1], p[2], kv.Value));
            }
            foreach (var kv in moveCount)
                report.Add(string.Format("{0} の {1} 行", kv.Key, kv.Value));

            bool changed = report.Count > 0;
            if (!changed) return report;

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var kv in final)
            {
                string path = kv.Key;
                var dedup = new Dictionary<string, string[]>();
                foreach (var r in kv.Value)
                {
                    string key = r[0] + "|" + r[2] + "|" + r[3] + "|" + r[11];
                    dedup[key] = r;
                }
                var outRows = dedup.Values
                    .OrderBy(r => r[0], StringComparer.Ordinal)
                    .ThenBy(r => r[2], StringComparer.Ordinal)
                    .ThenBy(r => r[3], StringComparer.Ordinal)
                    .ThenBy(r => r[11], StringComparer.Ordinal)
                    .ToList();

                string text = string.Join("\n", outRows.Select(r => string.Join("\t", r)));
                if (original.TryGetValue(path, out var oldText) && oldText == text) continue;
                if (File.Exists(path))
                {
                    try { File.Copy(path, path + "." + stamp + ".bak", true); }
                    catch { }
                }
                WriteRows(path, outRows);
            }
            return report;
        }

        private static void WriteRows(string path, List<string[]> rows)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join("\t", Header)).Append("\r\n");
            foreach (var r in rows) sb.Append(string.Join("\t", r)).Append("\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        public static void AppendSnapshot(string path, DateTime date, string label, SiState state)
        {
            bool newFile = !File.Exists(path);
            using (var w = new StreamWriter(path, true, new UTF8Encoding(true)))
            {
                if (newFile)
                {
                    for (int i = 0; i < Header.Length; i++)
                    {
                        if (i > 0) w.Write('\t');
                        w.Write(Header[i]);
                    }
                    w.WriteLine();
                }
                var keys = new List<int>(state.Tps.Keys);
                keys.Sort();
                foreach (int tk in keys)
                {
                    var tp = state.Tps[tk];
                    int onid = tk >> 16, tsid = tk & 0xFFFF;
                    var svcKeys = new List<long>();
                    foreach (var kv in state.Svcs)
                    {
                        if ((kv.Key >> 48) != onid) continue;
                        if (((kv.Key >> 16) & 0xFFFFFFFF) != tsid) continue;
                        svcKeys.Add(kv.Key);
                    }
                    svcKeys.Sort();
                    if (svcKeys.Count == 0) svcKeys.Add(SiState.SvcKey(onid, tsid, 0));
                    foreach (long sk in svcKeys)
                    {
                        var svc = state.Svcs.TryGetValue(sk, out var s) ? s : new SvcInfo();
                        int sid = (int)(sk & 0xFFFF);
                        var vals = new[]
                        {
                            date.ToString("yyyy-MM-dd HH:mm"), label, onid.ToString(), tsid.ToString(),
                            tp.FreqHz.ToString(), tp.Pol, tp.SymRate.ToString(), tp.Modulation,
                            tp.FecInner ?? "", tp.FecOuter ?? "", tp.System ?? "",
                            sid.ToString(), TsUtil.EscapeTsv(svc.Name), TsUtil.EscapeTsv(svc.Provider),
                            svc.Type.ToString()
                        };
                        w.WriteLine(string.Join("\t", vals));
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // 設定
    // ------------------------------------------------------------------

    internal class DiscordTarget
    {
        public string Name = "";
        public string Webhook = "";
        public string BotToken = "";
        public string ChannelId = "";
        public List<string> UserIds = new List<string>();
    }

    internal class Config
    {
        public List<DiscordTarget> Discord = new List<DiscordTarget>();
        public string DataDir = "data";
        public bool NotifyOnFirstRun = false;
        public Dictionary<string, string> Templates = new Dictionary<string, string>
        {
            { "DVB-C", "ケーブル(DVB-C)の周波数/チャンネルリストの変更が検知されました" },
            { "110BS", "110BSの周波数/チャンネルリストの変更が検知されました" },
            { "110", "110スカパー!の周波数/チャンネルリストの変更が検知されました" },
            { "124/128", "124/128スカパー!の周波数/チャンネルリストの変更が検知されました" },
        };
    }

    internal static class ConfigStore
    {
        public static Config Load(string path)
        {
            if (!File.Exists(path)) return null;
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var root = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            if (root == null) return null;
            var cfg = new Config();
            if (root.TryGetValue("dataDir", out var dd) && dd is string s1 && s1.Length > 0) cfg.DataDir = s1;
            if (root.TryGetValue("notifyOnFirstRun", out var nf) && nf is bool b1) cfg.NotifyOnFirstRun = b1;
            if (root.TryGetValue("templates", out var tpl) && tpl is Dictionary<string, object> tm)
            {
                foreach (var kv in tm) if (kv.Value is string s2) cfg.Templates[kv.Key] = s2;
            }
            // 注意: JavaScriptSerializer は JSON 配列を ArrayList で返すため、
            // IEnumerable<object> では決してマッチしない (これが原因で discord 設定が
            // 読めず「通知が来ない」状態になっていた)。非ジェネリックで受ける。
            if (root.TryGetValue("discord", out var dl) && dl is System.Collections.IEnumerable list && !(dl is string))
            {
                foreach (var item in list)
                {
                    if (!(item is Dictionary<string, object> d)) continue;
                    var t = new DiscordTarget
                    {
                        Name = d.TryGetValue("name", out var n) && n is string s3 ? s3 : "",
                        Webhook = d.TryGetValue("webhook", out var w) && w is string s4 ? s4 : "",
                        BotToken = d.TryGetValue("botToken", out var bt) && bt is string s5 ? s5 : "",
                        ChannelId = d.TryGetValue("channelId", out var ci) && ci is string s6 ? s6 : "",
                    };
                    if (d.TryGetValue("userIds", out var ui) && ui is System.Collections.IEnumerable ul && !(ui is string))
                        foreach (var u in ul) if (u is string s7 && s7.Length > 0) t.UserIds.Add(s7);
                    if (t.Webhook.Length > 0 || (t.BotToken.Length > 0 && t.ChannelId.Length > 0))
                        cfg.Discord.Add(t);
                }
            }
            return cfg;
        }

        public static void WriteSample(string path)
        {
            var sample = new Dictionary<string, object>
            {
                { "_readme", "discord[].webhook に Discord の Webhook URL、userIds にメンションしたいユーザーIDを設定してください。" +
                             "ボットトークンで投下する場合は botToken と channelId を設定します (webhook と併用可)。" +
                             "dataDir は TSV スナップショットの保存先 (exe からの相対パス可)。" +
                             "notifyOnFirstRun は初回 (データ未作成時) も通知するかどうか。" },
                { "dataDir", "data" },
                { "notifyOnFirstRun", false },
                { "discord", new object[] {
                    new Dictionary<string, object> {
                        { "name", "main" },
                        { "webhook", "https://discord.com/api/webhooks/XXXXXXXX/YYYYYYYY" },
                        { "userIds", new object[] { "123456789012345678" } },
                    } } },
            };
            var ser = new JavaScriptSerializer();
            File.WriteAllText(path, ser.Serialize(sample));
        }
    }

    // ------------------------------------------------------------------
    // ログ (EDCB の録画後 bat からは stdout/stderr が捨てられるため、必ずファイルに残す)
    // ------------------------------------------------------------------

    internal static class Log
    {
        public static string FilePath = "";
        private static readonly object Sync = new object();

        public static void Write(string line)
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            try
            {
                lock (Sync)
                    System.IO.File.AppendAllText(FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + "\r\n",
                        new UTF8Encoding(false));
            }
            catch { /* ログ失敗で本処理は止めない */ }
        }
    }

    // ------------------------------------------------------------------
    // Discord 通知
    // ------------------------------------------------------------------

    internal static class Discord
    {
        public static string BuildMessage(Config cfg, string label, DateTime detectTime, SiState state, DiffResult diff, int onid, string netName)
        {
            string template = cfg.Templates.TryGetValue(label, out var t) ? t
                : label + "の周波数/チャンネルリストの変更が検知されました";
            var sb = new StringBuilder();
            var mention = string.Join(" ", cfg.Discord.SelectMany(x => x.UserIds).Distinct().Select(id => "<@" + id + ">"));
            if (mention.Length > 0) sb.Append(mention).Append('\n');
            sb.Append(template).Append('\n');
            sb.Append("検知時刻: ").Append(detectTime.ToString("yyyy-MM-dd HH:mm")).Append('\n');
            sb.Append("対象: ONID ").Append(onid).Append(netName.Length > 0 ? " (" + netName + ")" : "").Append('\n');
            sb.Append("トランスポンダ: 追加").Append(diff.TpAdded.Count).Append(" 削除").Append(diff.TpRemoved.Count)
              .Append(" 変更").Append(diff.TpChanged.Count).Append('\n');
            sb.Append("サービス: 追加").Append(diff.SvcAdded.Count).Append(" 削除").Append(diff.SvcRemoved.Count)
              .Append(" 移動").Append(diff.SvcMoved.Count).Append(" 変更").Append(diff.SvcChanged.Count).Append('\n');

            var details = new List<string>();
            details.AddRange(diff.TpAdded.Select(x => "[追加TP] " + x));
            details.AddRange(diff.TpRemoved.Select(x => "[削除TP] " + x));
            details.AddRange(diff.TpChanged.Select(x => "[変更TP] " + x));
            details.AddRange(diff.SvcMoved.Select(x => "[移動SVC] " + x));
            details.AddRange(diff.SvcAdded.Select(x => "[追加SVC] " + x));
            details.AddRange(diff.SvcRemoved.Select(x => "[削除SVC] " + x));
            details.AddRange(diff.SvcChanged.Select(x => "[変更SVC] " + x));

            if (details.Count > 0)
            {
                sb.Append('\n');
                int shown = 0;
                foreach (var line in details)
                {
                    if (sb.Length + line.Length + 30 > 1900)
                    {
                        sb.Append("… 他 ").Append(details.Count - shown).Append(" 件\n");
                        break;
                    }
                    sb.Append(line).Append('\n');
                    shown++;
                }
            }
            return sb.ToString().TrimEnd();
        }

        public static List<string> Validate(Config cfg)
        {
            var w = new List<string>();
            if (cfg.Discord.Count == 0)
                w.Add("config.json に有効な discord 設定がありません (webhook か botToken+channelId を設定してください)");
            foreach (var t in cfg.Discord)
            {
                string nm = t.Name.Length > 0 ? t.Name : "target";
                if (t.Webhook.Length > 0)
                {
                    if (t.Webhook.Contains("XXXX") || t.Webhook.Contains("YYYY"))
                        w.Add("discord[" + nm + "].webhook がサンプルのままです: " + t.Webhook);
                    else if (!t.Webhook.StartsWith("https://discord.com/api/webhooks/") &&
                             !t.Webhook.StartsWith("https://discordapp.com/api/webhooks/"))
                        w.Add("discord[" + nm + "].webhook の形式が Discord の Webhook URL ではありません: " + t.Webhook);
                }
            }
            return w;
        }

        public static async System.Threading.Tasks.Task<int> SendAll(Config cfg, string message, bool dryRun)
        {
            if (dryRun)
            {
                Console.WriteLine("=== Discord 投下内容 (dry-run) ===");
                Console.WriteLine(message);
                Log.Write("[dry-run] 投下内容を表示 (" + message.Length + " 文字)");
                return 0;
            }
            if (cfg.Discord.Count == 0)
            {
                string w = "[警告] config.json に discord 設定がありません。通知はスキップされました。";
                Console.Error.WriteLine(w);
                Log.Write(w);
                return 0;
            }
            int failures = 0;
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(20);
                foreach (var t in cfg.Discord)
                {
                    string nm = t.Name.Length > 0 ? t.Name : "target";
                    try
                    {
                        var payload = "{\"content\":\"" + TsUtil.JsonEscape(message) +
                            "\",\"allowed_mentions\":{\"parse\":[\"users\"]}}";
                        var content = new StringContent(payload, Encoding.UTF8, "application/json");
                        HttpResponseMessage resp;
                        if (t.Webhook.Length > 0)
                        {
                            resp = await http.PostAsync(t.Webhook.Trim(), content);
                        }
                        else
                        {
                            var req = new HttpRequestMessage(HttpMethod.Post,
                                "https://discord.com/api/v10/channels/" + t.ChannelId + "/messages");
                            req.Headers.TryAddWithoutValidation("Authorization", "Bot " + t.BotToken);
                            req.Content = content;
                            resp = await http.SendAsync(req);
                        }
                        string body = "";
                        try { if (resp.Content != null) body = await resp.Content.ReadAsStringAsync(); }
                        catch { }
                        if ((int)resp.StatusCode >= 300)
                        {
                            failures++;
                            if (body.Length > 400) body = body.Substring(0, 400) + "…";
                            string w = string.Format("[警告] Discord 送信失敗 ({0}): HTTP {1} {2}",
                                nm, (int)resp.StatusCode, body.Replace("\r", " ").Replace("\n", " "));
                            Console.Error.WriteLine(w);
                            Log.Write(w);
                        }
                        else
                        {
                            Log.Write(string.Format("[通知] {0} へ送信 OK (HTTP {1}, {2} 文字)", nm, (int)resp.StatusCode, message.Length));
                        }
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        string detail = ex.InnerException != null ? ex.Message + " / " + ex.InnerException.Message : ex.Message;
                        string w = string.Format("[警告] Discord 送信エラー ({0}): {1}", nm, detail);
                        Console.Error.WriteLine(w);
                        Log.Write(w);
                    }
                }
            }
            return failures;
        }
    }

    // ------------------------------------------------------------------
    // メイン
    // ------------------------------------------------------------------

    internal static class Program
    {
        /// <summary>
        /// ファイル名から録画日時を取り出す (EDCB の命名: …-2026_10_14_23_30.m2ts)。
        /// 複数候補があるときは最後のもの (EDCB は末尾に日時を付ける) を採用する。
        /// </summary>
        private static DateTime? DateFromFileName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var ms = Regex.Matches(name, @"(?<![0-9])(19|20)[0-9]{2}_[0-9]{2}_[0-9]{2}_[0-9]{2}_[0-9]{2}(?![0-9])");
            if (ms.Count == 0) return null;
            DateTime dt;
            if (DateTime.TryParseExact(ms[ms.Count - 1].Value, "yyyy_MM_dd_HH_mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                return dt;
            return null;
        }

        private static int Main(string[] args)
        {
            try
            {
                string input = null, configPath = null;
                bool dryRun = false, quiet = false, testNotify = false;
                for (int i = 0; i < args.Length; i++)
                {
                    var a = args[i];
                    if (a == "--config" && i + 1 < args.Length) configPath = args[++i];
                    else if (a == "--dry-run") dryRun = true;
                    else if (a == "--quiet") quiet = true;
                    else if (a == "--test-notify") testNotify = true;
                    else if (a.StartsWith("-")) { /* unknown option: ignore */ }
                    else if (input == null) input = a;
                }

                // .NET Framework 4.8 の既定プロトコルは SSL3/TLS1.0 のことがあり、
                // TLS1.2 以上が必須の Discord で
                // 「SSL/TLS のセキュリティで保護されたチャネルを作成できませんでした」
                // になる。送信前に明示的に有効化する。
                try
                {
                    // TLS1.2 + TLS1.3 (12288)。OS が 1.3 非対応でも 1.2 で交渉される。
                    ServicePointManager.SecurityProtocol =
                        SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
                }
                catch
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                }

                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                if (configPath == null) configPath = Path.Combine(exeDir, "config.json");
                Config cfg;
                if (!File.Exists(configPath))
                {
                    ConfigStore.WriteSample(configPath);
                    Console.Error.WriteLine("[情報] config.json を生成しました。Discord に通知する場合は編集してください。");
                    cfg = new Config();
                }
                else cfg = ConfigStore.Load(configPath);
                if (cfg == null) { Console.Error.WriteLine("[エラー] config.json を読み取れませんでした。"); return 1; }

                string dataDir = Path.IsPathRooted(cfg.DataDir) ? cfg.DataDir : Path.Combine(exeDir, cfg.DataDir);
                Directory.CreateDirectory(dataDir);
                Log.FilePath = Path.Combine(dataDir, "TsSiWatch.log");

                foreach (var w in Discord.Validate(cfg))
                {
                    Console.Error.WriteLine("[警告] " + w);
                    Log.Write("[警告] " + w);
                }

                if (testNotify)
                {
                    if (cfg.Discord.Count == 0)
                    {
                        Console.Error.WriteLine("[エラー] 送信先がありません (config.json の discord を設定してください)");
                        Log.Write("[エラー] --test-notify: 送信先なし");
                        return 4;
                    }
                    string test = "TsSiWatch テスト通知です (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ")";
                    var t0 = Discord.SendAll(cfg, test, false);
                    t0.Wait();
                    Console.WriteLine(t0.Result == 0 ? "テスト通知: 送信 OK" : "テスト通知: 失敗 " + t0.Result + " 件 (TsSiWatch.log を確認)");
                    return t0.Result == 0 ? 0 : 4;
                }

                if (input == null)
                {
                    Console.WriteLine("使い方: TsSiWatch.exe <録画.m2ts> [--config config.json] [--dry-run] [--quiet]");
                    Console.WriteLine("      TsSiWatch.exe --test-notify   (Discord 設定の疎通確認)");
                    return 1;
                }
                if (!File.Exists(input))
                {
                    Console.Error.WriteLine("[エラー] ファイルが見つかりません: " + input);
                    Log.Write("[エラー] ファイルが見つかりません: " + input);
                    return 1;
                }

                Log.Write("=== 開始: " + input + (dryRun ? " (dry-run)" : ""));
                if (!quiet) Console.WriteLine("解析中: " + input);
                var sw = Stopwatch.StartNew();
                var state = Scanner.Scan(input);
                sw.Stop();
                if (!quiet)
                    Console.WriteLine("解析完了: {0} packets, {1}-byte, CRCエラー {2} ({3:F1}s)",
                        state.Packets, state.PacketSize, state.CrcErrors, sw.Elapsed.TotalSeconds);
                Log.Write(string.Format("解析完了: {0} packets, CRCエラー {1} ({2:F1}s)",
                    state.Packets, state.CrcErrors, sw.Elapsed.TotalSeconds));
                if (state.Tps.Count == 0)
                {
                    Console.Error.WriteLine("[エラー] NIT が見つかりませんでした。BS/CS の録画ですか?");
                    Log.Write("[エラー] NIT が見つかりませんでした");
                    return 1;
                }

                // 検知日付は「ファイル名の日時」を第一候補にする。
                //   * ファイルの更新日時はコピー / 展開 / rclone 移動 / 録画後のリネームで
                //     簡単に壊れるので使わない。
                //   * 放送波の時刻 (TDT/TOT) もヘッドエンドによっては数時間〜1年ずれる
                //     (実測: BS で +9.5h、スカパー!系 DVB-C で約 1 年)。
                //   * よって EDCB の命名規則 (…-2026_10_14_23_30.m2ts) から取れた日時を
                //     採用し、取れないときだけ TDT/TOT、それも無ければ実行時刻にする。
                DateTime detectTime;
                DateTime? nameTime = DateFromFileName(input);
                if (nameTime.HasValue)
                {
                    detectTime = nameTime.Value;
                    if (state.TdtTime.HasValue)
                    {
                        double hours = (state.TdtTime.Value - nameTime.Value).TotalHours;
                        if (Math.Abs(hours) > 3)
                        {
                            string w = string.Format(
                                "[注意] 放送波の時刻 ({0:yyyy-MM-dd HH:mm}) がファイル名の日時 ({1:yyyy-MM-dd HH:mm}) と {2:F1} 時間ずれています (ヘッドエンドの時計異常)。検知日付はファイル名を採用します",
                                state.TdtTime.Value, nameTime.Value, hours);
                            Console.Error.WriteLine(w);
                            Log.Write(w);
                        }
                    }
                }
                else if (state.TdtTime.HasValue)
                {
                    detectTime = state.TdtTime.Value;
                    string w = string.Format(
                        "[注意] ファイル名に日時が無いため放送波の時刻 ({0:yyyy-MM-dd HH:mm}) を使用します (ヘッドエンドによっては狂います)",
                        state.TdtTime.Value);
                    Console.Error.WriteLine(w);
                    Log.Write(w);
                }
                else
                {
                    detectTime = DateTime.Now;
                    string w = "[注意] ファイル名にも TDT/TOT にも日時が無いため、実行時刻を検知日付にします: " +
                        detectTime.ToString("yyyy-MM-dd HH:mm");
                    Console.Error.WriteLine(w);
                    Log.Write(w);
                }

                // 旧バージョンが 1 ファイルに混ぜてしまったネットワークを整理する (冪等)
                foreach (var line in TsvStore.Migrate(dataDir))
                {
                    Console.Error.WriteLine("[移行] " + line);
                    Log.Write("[移行] " + line);
                }

                // ネットワーク (ONID) ごとに分割し、それぞれ別ファイルで比較・通知する。
                // 1 本の録画に NIT actual と NIT other が混在するため、ファイル単位で
                // 1 ラベルにまとめると 110CS 録画が 110BS の履歴を壊す。
                var groups = Classifier.Split(state);
                if (groups.Count == 0)
                {
                    Console.Error.WriteLine("[エラー] NIT actual に有効なトランスポンダがありませんでした");
                    Log.Write("[エラー] NIT actual に有効な TP なし");
                    return 1;
                }

                int failures = 0;
                foreach (var grp in groups)
                {
                    var gs = grp.State;
                    string label = grp.Label;
                    string netName = gs.NetworkNames.TryGetValue(grp.OnId, out var nm) ? nm : "";
                    string head = string.Format("種別: {0} / ONID {1}{2} / TP {3} / サービス {4} / 検知日付: {5:yyyy-MM-dd HH:mm}",
                        label, grp.OnId, netName.Length > 0 ? " " + netName : "", gs.Tps.Count, gs.Svcs.Count, detectTime);
                    if (!quiet) Console.WriteLine(head);
                    Log.Write(head);

                    string tsvPath = Path.Combine(dataDir, SiState.SanitizeLabel(label) + ".tsv");
                    var rows = TsvStore.LoadRows(tsvPath);
                    var myRows = new List<string[]>();
                    foreach (var r in rows)
                        if (int.Parse(r[2]) == grp.OnId) myRows.Add(r);
                    var latest = TsvStore.LatestDate(myRows);
                    var oldState = myRows.Count == 0 ? null
                        : TsvStore.BuildState(myRows.Where(r => r[0] == latest.ToString("yyyy-MM-dd HH:mm")).ToList());

                    // 名前が取れなかったサービスは前回値を引き継ぐ (空 → 変更 の誤検知防止)
                    Differ.CarryOverNames(oldState, gs);

                    var diff = Differ.Diff(oldState ?? new SiState(), gs);
                    bool firstRun = oldState == null || oldState.Tps.Count == 0;
                    bool changed = firstRun || diff.Any();

                    if (changed) TsvStore.AppendSnapshot(tsvPath, detectTime, label, gs);
                    if (!quiet) Console.WriteLine(changed ? "TSV 更新: " + tsvPath : "変更なし: " + tsvPath);
                    Log.Write(changed
                        ? string.Format("TSV 更新: {0} (TP +{1} -{2} ~{3} / SVC +{4} -{5} 移動{6} ~{7})",
                            tsvPath, diff.TpAdded.Count, diff.TpRemoved.Count, diff.TpChanged.Count,
                            diff.SvcAdded.Count, diff.SvcRemoved.Count, diff.SvcMoved.Count, diff.SvcChanged.Count)
                        : "変更なし: " + tsvPath);

                    if (!changed) continue;
                    if (firstRun && !cfg.NotifyOnFirstRun)
                    {
                        string w = "初回のため Discord 投下はスキップ (通知するには config.json の notifyOnFirstRun を true に)";
                        if (!quiet) Console.WriteLine(w);
                        Log.Write(w);
                        continue;
                    }

                    string message = Discord.BuildMessage(cfg, label, detectTime, gs, diff, grp.OnId, netName);
                    var task = Discord.SendAll(cfg, message, dryRun);
                    task.Wait();
                    failures += task.Result;
                }

                return failures == 0 ? 0 : 4;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[エラー] " + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                Log.Write("[エラー] " + ex.Message);
                return 1;
            }
        }
    }
}
