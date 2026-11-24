using System.Text;

namespace Pane.Services;

public static class QrCode
{
    // ISO/IEC 18004：按版本列出总码字数、M 级分块数、每块纠错码字数。
    static readonly int[] Total = [0,26,44,70,100,134,172,196,242,292,346,404,466,532,581,655];
    static readonly int[] Blocks = [0,1,1,1,2,2,4,4,4,5,5,5,8,9,9,10];
    static readonly int[] Parity = [0,10,16,26,18,24,16,18,22,22,26,30,22,22,24,24];

    /// <summary>编码成方阵，以 [行, 列] 取模块，true 为黑。不含静区；超出版本 15 的容量时抛 ArgumentException。</summary>
    public static bool[,] Encode(string text) => MaskCandidates(text).MinBy(Penalty)!;

    // 按标准编号依次产生候选矩阵；罚分相同取先遇到的掩码。
    internal static IEnumerable<bool[,]> MaskCandidates(string text)
    {
        int count = Encoding.UTF8.GetByteCount(text);
        for (int version = 1; version < Total.Length; version++)
        {
            int capacity = Total[version] - Blocks[version] * Parity[version];
            int lengthBits = version < 10 ? 8 : 16;
            if (count > capacity - (4 + lengthBits + 7) / 8) continue;
            var data = new byte[capacity];
            int bit = 0;
            void Write(int value, int width)
            {
                for (int i = width - 1; i >= 0; i--, bit++)
                    data[bit / 8] |= (byte)(((value >> i) & 1) << (7 - bit % 8));
            }
            Write(4, 4);
            Write(count, lengthBits);
            foreach (byte b in Encoding.UTF8.GetBytes(text)) Write(b, 8);
            Write(0, 4);
            for (int i = bit / 8, pad = 0xec; i < capacity; i++, pad ^= 0xfd) data[i] = (byte)pad;
            return Draw(version, Interleave(data, version));
        }
        throw new ArgumentException("内容超过二维码版本 15 的容量。", nameof(text));
    }

    // 在 GF(256) 上按本原多项式 x⁸+x⁴+x³+x²+1 运算。
    static byte Multiply(int a, int b)
    {
        int product = 0;
        while (b != 0)
        {
            if ((b & 1) != 0) product ^= a;
            b >>= 1;
            a = (a << 1) ^ ((a & 128) != 0 ? 0x11d : 0);
        }
        return (byte)product;
    }

    static byte[] Interleave(byte[] data, int version)
    {
        int degree = Parity[version], blocks = Blocks[version];
        byte[] generator = new byte[degree + 1];
        generator[0] = 1;
        for (int i = 0, root = 1; i < degree; i++, root = Multiply(root, 2))
            for (int j = i + 1; j > 0; j--)
                generator[j] ^= Multiply(generator[j - 1], root);

        var result = new byte[Total[version]];
        int shortSize = data.Length / blocks, shortCount = blocks - data.Length % blocks;
        int offset = 0;
        for (int b = 0; b < blocks; b++)
        {
            int length = shortSize + (b >= shortCount ? 1 : 0);
            byte[] remainder = new byte[length + degree];
            data.AsSpan(offset, length).CopyTo(remainder);
            for (int i = 0; i < length; i++)
            {
                result[i * blocks + b - (i == shortSize ? shortCount : 0)] = data[offset + i];
                byte factor = remainder[i];
                for (int j = 1; j <= degree; j++) remainder[i + j] ^= Multiply(factor, generator[j]);
            }
            for (int i = 0; i < degree; i++) result[data.Length + i * blocks + b] = remainder[length + i];
            offset += length;
        }
        return result;
    }

    static int Bch(int value, int polynomial, int degree)
    {
        int shifted = value << degree, remainder = shifted;
        for (int i = 5; i >= 0; i--)
            if ((remainder & (1 << (degree + i))) != 0) remainder ^= polynomial << i;
        return shifted | remainder;
    }

    static IEnumerable<bool[,]> Draw(int version, byte[] bytes)
    {
        int size = 17 + 4 * version;
        var modules = new bool[size, size];
        var reserved = new bool[size, size];
        void Set(int x, int y, bool dark)
        {
            reserved[y, x] = true;
            modules[y, x] = dark;
        }
        void Target(int cx, int cy, int radius)
        {
            for (int y = Math.Max(0, cy - radius); y <= Math.Min(size - 1, cy + radius); y++)
                for (int x = Math.Max(0, cx - radius); x <= Math.Min(size - 1, cx + radius); x++)
                {
                    int distance = Math.Max(Math.Abs(x - cx), Math.Abs(y - cy));
                    Set(x, y, radius == 2 ? distance != 1 : distance != 2 && distance != 4);
                }
        }
        for (int i = 0; i < size; i++)
        {
            Set(6, i, i % 2 == 0);
            Set(i, 6, i % 2 == 0);
        }
        Target(3, 3, 4);
        Target(size - 4, 3, 4);
        Target(3, size - 4, 4);
        if (version > 1)
        {
            int count = version / 7 + 2;
            int step = ((size - 13 + 2 * count - 3) / (2 * count - 2)) * 2;
            int[] centers = new int[count];
            centers[0] = 6;
            for (int i = 1; i < count; i++) centers[i] = size - 7 - (count - 1 - i) * step;
            foreach (int y in centers)
                foreach (int x in centers)
                    if (!(x == 6 && (y == 6 || y == size - 7) || x == size - 7 && y == 6))
                        Target(x, y, 2);
        }
        if (version >= 7)
        {
            int info = Bch(version, 0x1f25, 12);
            for (int i = 0; i < 18; i++)
            {
                bool dark = ((info >> i) & 1) != 0;
                int a = size - 11 + i % 3, b = i / 3;
                Set(a, b, dark);
                Set(b, a, dark);
            }
        }
        void Format(int mask)
        {
            int info = Bch(mask, 0x537, 10) ^ 0x5412; // M 级的格式前缀为 00。
            for (int i = 0; i < 15; i++)
            {
                bool dark = ((info >> i) & 1) != 0;
                if (i < 8) Set(8, i < 6 ? i : i + 1, dark);
                else Set(i == 8 ? 7 : 14 - i, 8, dark);
                if (i < 8) Set(size - 1 - i, 8, dark);
                else Set(8, size - 15 + i, dark);
            }
            Set(8, size - 8, true);
        }
        Format(0);
        int bit = 0;
        bool upward = true;
        for (int right = size - 1; right > 0; right -= 2)
        {
            if (right == 6) right--;
            for (int row = 0; row < size; row++)
            {
                int y = upward ? size - 1 - row : row;
                for (int x = right; x >= right - 1; x--)
                {
                    if (reserved[y, x]) continue;
                    modules[y, x] = bit < bytes.Length * 8 && ((bytes[bit / 8] >> (7 - bit % 8)) & 1) != 0;
                    bit++;
                }
            }
            upward = !upward;
        }
        for (int mask = 0; mask < 8; mask++)
        {
            Format(mask);
            var candidate = (bool[,])modules.Clone();
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (!reserved[y, x]) candidate[y, x] ^= Mask(mask, x, y);
            yield return candidate;
        }
    }

    static bool Mask(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0, 1 => y % 2 == 0, 2 => x % 3 == 0, 3 => (x + y) % 3 == 0,
        4 => (y / 2 + x / 3) % 2 == 0, 5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0, _ => ((x + y) % 2 + x * y % 3) % 2 == 0
    };

    static int Penalty(bool[,] matrix)
    {
        int size = matrix.GetLength(0), score = 0, dark = 0;
        int[] runs = new int[size + 2];
        for (int line = 0; line < size; line++)
        {
            for (int direction = 0; direction < 2; direction++)
            {
                int last = 0;
                runs[0] = 0;
                bool color = false;
                for (int i = 0; i < size; i++)
                {
                    bool cell = direction == 0 ? matrix[line, i] : matrix[i, line];
                    if (color != cell) { runs[++last] = 0; color = cell; }
                    runs[last]++;
                }
                for (int i = 0; i <= last; i++)
                    if (runs[i] >= 5) score += runs[i] - 2;
                // 将边界外的白色静区计入定位图案两端的留白，但不计入连续同色罚分。
                runs[0] += size;
                if (color) runs[++last] = 0;
                runs[last] += size;
                for (int i = 1; i + 5 <= last; i += 2)
                {
                    int n = runs[i];
                    if (runs[i+1] != n || runs[i+2] != 3*n || runs[i+3] != n || runs[i+4] != n) continue;
                    if (runs[i-1] >= 4*n && runs[i+5] >= n) score += 40;
                    if (runs[i-1] >= n && runs[i+5] >= 4*n) score += 40;
                }
            }
            for (int x = 0; x < size; x++)
            {
                bool value = matrix[line, x];
                if (value) dark++;
                if (line > 0 && x > 0 && matrix[line-1,x] == value && matrix[line,x-1] == value && matrix[line-1,x-1] == value)
                    score += 3;
            }
        }
        return score + 10 * (Math.Abs(20 * dark - 10 * size * size) / (size * size));
    }
}
