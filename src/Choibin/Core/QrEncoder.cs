using System;
using System.Text;

namespace Choibin.Core
{
    /// <summary>
    /// 依存ライブラリなしのQRコード生成器。
    /// バイトモード / 誤り訂正レベルM / 型番1〜6（最大106バイト）に対応。
    /// スマホに読ませるLAN内URLを表示する用途に十分な範囲です。
    /// </summary>
    public static class QrEncoder
    {
        // 型番ごとの (1ブロックあたりの誤り訂正コード語数, [(ブロック数, データコード語数), ...]) レベルM
        private static readonly int[] EccPerBlock = { 0, 10, 16, 26, 18, 24, 16 };
        private static readonly int[][][] Groups =
        {
            null,
            new[] { new[] { 1, 16 } },
            new[] { new[] { 1, 28 } },
            new[] { new[] { 1, 44 } },
            new[] { new[] { 2, 32 } },
            new[] { new[] { 2, 43 } },
            new[] { new[] { 4, 27 } },
        };
        private static readonly int[][] AlignCenters =
        {
            null,
            new int[0],
            new[] { 6, 18 },
            new[] { 6, 22 },
            new[] { 6, 26 },
            new[] { 6, 30 },
            new[] { 6, 34 },
        };

        private static readonly int[] Exp = new int[512];
        private static readonly int[] Log = new int[256];

        static QrEncoder()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                Exp[i] = x;
                Log[x] = i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        }

        private static int Mul(int a, int b)
        {
            if (a == 0 || b == 0) return 0;
            return Exp[Log[a] + Log[b]];
        }

        /// <summary>文字列をQRコードのモジュール行列（true=黒）に変換します。</summary>
        public static bool[,] Encode(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text ?? string.Empty);
            int version = ChooseVersion(data.Length);
            int[] codewords = BuildCodewords(data, version);
            int size = 17 + version * 4;

            bool[,] best = null;
            int bestScore = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                bool[,] m = BuildMatrix(version, codewords, mask);
                PlaceFormat(m, size, 0, mask);
                int score = Penalty(m, size);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = m;
                }
            }
            return best;
        }

        private static int ChooseVersion(int byteCount)
        {
            for (int v = 1; v <= 6; v++)
            {
                if (byteCount + 2 <= DataCodewordCount(v)) return v;
            }
            throw new ArgumentException("QRコードに収まりません（最大106バイト）。");
        }

        private static int DataCodewordCount(int version)
        {
            int total = 0;
            foreach (int[] g in Groups[version]) total += g[0] * g[1];
            return total;
        }

        private static int[] BuildCodewords(byte[] data, int version)
        {
            int dcTotal = DataCodewordCount(version);
            int capacityBits = dcTotal * 8;

            var bits = new System.Collections.Generic.List<int>(capacityBits);
            Action<int, int> put = (value, count) =>
            {
                for (int i = count - 1; i >= 0; i--) bits.Add((value >> i) & 1);
            };

            put(0x4, 4);                 // バイトモード
            put(data.Length, 8);         // 文字数（型番1〜9は8ビット）
            foreach (byte b in data) put(b, 8);

            int remaining = capacityBits - bits.Count;
            put(0, Math.Min(4, remaining));
            while (bits.Count % 8 != 0) bits.Add(0);

            var cw = new System.Collections.Generic.List<int>(dcTotal);
            for (int i = 0; i < bits.Count; i += 8)
            {
                int v = 0;
                for (int j = 0; j < 8; j++) v = (v << 1) | bits[i + j];
                cw.Add(v);
            }
            int[] pad = { 0xEC, 0x11 };
            int p = 0;
            while (cw.Count < dcTotal) cw.Add(pad[p++ % 2]);

            // ブロック分割 → 誤り訂正 → インターリーブ
            int eccLen = EccPerBlock[version];
            var blocks = new System.Collections.Generic.List<int[]>();
            var eccs = new System.Collections.Generic.List<int[]>();
            int pos = 0;
            foreach (int[] g in Groups[version])
            {
                for (int i = 0; i < g[0]; i++)
                {
                    var block = new int[g[1]];
                    cw.CopyTo(pos, block, 0, g[1]);
                    pos += g[1];
                    blocks.Add(block);
                    eccs.Add(ReedSolomon(block, eccLen));
                }
            }

            var result = new System.Collections.Generic.List<int>();
            int maxLen = 0;
            foreach (int[] b in blocks) maxLen = Math.Max(maxLen, b.Length);
            for (int i = 0; i < maxLen; i++)
                foreach (int[] b in blocks)
                    if (i < b.Length) result.Add(b[i]);
            for (int i = 0; i < eccLen; i++)
                foreach (int[] e in eccs)
                    result.Add(e[i]);

            return result.ToArray();
        }

        private static int[] ReedSolomon(int[] data, int eccLen)
        {
            int[] gen = { 1 };
            for (int i = 0; i < eccLen; i++)
            {
                var next = new int[gen.Length + 1];
                for (int j = 0; j < gen.Length; j++)
                {
                    next[j] ^= gen[j];
                    next[j + 1] ^= Mul(gen[j], Exp[i]);
                }
                gen = next;
            }

            var res = new int[data.Length + eccLen];
            Array.Copy(data, res, data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                int coef = res[i];
                if (coef == 0) continue;
                for (int j = 1; j < gen.Length; j++)
                    res[i + j] ^= Mul(gen[j], coef);
            }

            var ecc = new int[eccLen];
            Array.Copy(res, data.Length, ecc, 0, eccLen);
            return ecc;
        }

        private static bool MaskBit(int mask, int r, int c)
        {
            switch (mask)
            {
                case 0: return (r + c) % 2 == 0;
                case 1: return r % 2 == 0;
                case 2: return c % 3 == 0;
                case 3: return (r + c) % 3 == 0;
                case 4: return ((r / 2) + (c / 3)) % 2 == 0;
                case 5: return ((r * c) % 2 + (r * c) % 3) == 0;
                case 6: return (((r * c) % 2 + (r * c) % 3) % 2) == 0;
                default: return (((r + c) % 2 + (r * c) % 3) % 2) == 0;
            }
        }

        private static bool[,] BuildMatrix(int version, int[] codewords, int mask)
        {
            int size = 17 + version * 4;
            var m = new bool[size, size];
            var reserved = new bool[size, size];

            Action<int, int, bool> setFn = (r, c, v) =>
            {
                m[r, c] = v;
                reserved[r, c] = true;
            };

            // 位置検出パターンと分離パターン
            int[][] finders = { new[] { 0, 0 }, new[] { 0, size - 7 }, new[] { size - 7, 0 } };
            foreach (int[] f in finders)
            {
                for (int dr = -1; dr <= 7; dr++)
                {
                    for (int dc = -1; dc <= 7; dc++)
                    {
                        int r = f[0] + dr, c = f[1] + dc;
                        if (r < 0 || r >= size || c < 0 || c >= size) continue;
                        bool inside = dr >= 0 && dr < 7 && dc >= 0 && dc < 7;
                        bool v = false;
                        if (inside)
                            v = dr == 0 || dr == 6 || dc == 0 || dc == 6 ||
                                (dr >= 2 && dr <= 4 && dc >= 2 && dc <= 4);
                        setFn(r, c, v);
                    }
                }
            }

            // タイミングパターン
            for (int i = 0; i < size; i++)
            {
                if (!reserved[6, i]) setFn(6, i, i % 2 == 0);
                if (!reserved[i, 6]) setFn(i, 6, i % 2 == 0);
            }

            // 位置合わせパターン
            int[] centers = AlignCenters[version];
            foreach (int r in centers)
            {
                foreach (int c in centers)
                {
                    bool overlapsFinder = (r <= 8 && c <= 8) || (r <= 8 && c >= size - 9) || (r >= size - 9 && c <= 8);
                    if (overlapsFinder) continue;
                    for (int dr = -2; dr <= 2; dr++)
                        for (int dc = -2; dc <= 2; dc++)
                            setFn(r + dr, c + dc, Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1);
                }
            }

            // 常に黒のモジュール
            setFn(size - 8, 8, true);

            // 形式情報の領域を予約
            for (int i = 0; i <= 8; i++)
            {
                if (!reserved[8, i]) setFn(8, i, false);
                if (!reserved[i, 8]) setFn(i, 8, false);
            }
            for (int i = 0; i < 8; i++)
            {
                if (!reserved[8, size - 1 - i]) setFn(8, size - 1 - i, false);
                if (!reserved[size - 1 - i, 8]) setFn(size - 1 - i, 8, false);
            }

            // データ配置（右下から2列ずつジグザグ）
            int bitIndex = 0;
            int totalBits = codewords.Length * 8;
            int col = size - 1;
            bool upward = true;
            while (col > 0)
            {
                if (col == 6) col--;
                for (int k = 0; k < size; k++)
                {
                    int r = upward ? size - 1 - k : k;
                    for (int t = 0; t < 2; t++)
                    {
                        int c = col - t;
                        if (reserved[r, c]) continue;
                        bool bit = false;
                        if (bitIndex < totalBits)
                            bit = ((codewords[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) == 1;
                        bitIndex++;
                        if (MaskBit(mask, r, c)) bit = !bit;
                        m[r, c] = bit;
                    }
                }
                upward = !upward;
                col -= 2;
            }

            return m;
        }

        private static void PlaceFormat(bool[,] m, int size, int eccLevelBits, int mask)
        {
            int data = (eccLevelBits << 3) | mask;
            int rem = data;
            for (int i = 0; i < 10; i++)
                rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;

            Func<int, bool> bit = i => ((bits >> i) & 1) == 1;

            for (int i = 0; i < 6; i++) m[i, 8] = bit(i);
            m[7, 8] = bit(6);
            m[8, 8] = bit(7);
            m[8, 7] = bit(8);
            for (int i = 9; i < 15; i++) m[8, 14 - i] = bit(i);

            for (int i = 0; i < 8; i++) m[8, size - 1 - i] = bit(i);
            for (int i = 8; i < 15; i++) m[size - 15 + i, 8] = bit(i);
            m[size - 8, 8] = true;
        }

        private static int Penalty(bool[,] m, int size)
        {
            int score = 0;

            // 規則1: 同色の連続
            for (int pass = 0; pass < 2; pass++)
            {
                for (int a = 0; a < size; a++)
                {
                    int run = 1;
                    for (int b = 1; b < size; b++)
                    {
                        bool cur = pass == 0 ? m[a, b] : m[b, a];
                        bool prev = pass == 0 ? m[a, b - 1] : m[b - 1, a];
                        if (cur == prev) run++;
                        else
                        {
                            if (run >= 5) score += 3 + (run - 5);
                            run = 1;
                        }
                    }
                    if (run >= 5) score += 3 + (run - 5);
                }
            }

            // 規則2: 2x2の同色ブロック
            for (int r = 0; r < size - 1; r++)
                for (int c = 0; c < size - 1; c++)
                    if (m[r, c] == m[r, c + 1] && m[r, c] == m[r + 1, c] && m[r, c] == m[r + 1, c + 1])
                        score += 3;

            // 規則3: 1:1:3:1:1 パターン
            int[] pat1 = { 1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0 };
            int[] pat2 = { 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1 };
            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c <= size - 11; c++)
                {
                    bool a = true, b = true;
                    for (int i = 0; i < 11; i++)
                    {
                        int v = m[r, c + i] ? 1 : 0;
                        if (v != pat1[i]) a = false;
                        if (v != pat2[i]) b = false;
                    }
                    if (a || b) score += 40;
                }
            }
            for (int c = 0; c < size; c++)
            {
                for (int r = 0; r <= size - 11; r++)
                {
                    bool a = true, b = true;
                    for (int i = 0; i < 11; i++)
                    {
                        int v = m[r + i, c] ? 1 : 0;
                        if (v != pat1[i]) a = false;
                        if (v != pat2[i]) b = false;
                    }
                    if (a || b) score += 40;
                }
            }

            // 規則4: 黒モジュールの比率
            int dark = 0;
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (m[r, c]) dark++;
            int pct = dark * 100 / (size * size);
            int prevStep = (pct / 5) * 5;
            int nextStep = prevStep + 5;
            score += Math.Min(Math.Abs(prevStep - 50) / 5, Math.Abs(nextStep - 50) / 5) * 10;

            return score;
        }
    }
}
