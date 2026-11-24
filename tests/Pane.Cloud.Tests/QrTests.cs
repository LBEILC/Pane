using System.Text;
using Pane.Services;

internal static class QrTests
{
    // M 级的两份格式信息查表校验，数据容量 / 短块结构取自标准分块表。
    static readonly int[] Formats = [0x5412,0x5125,0x5e7c,0x5b4b,0x45f9,0x40ce,0x4f97,0x4aa0];
    static readonly int[] Capacities = [14,26,42,62,84,106,122,152,180,213,251,287,331,362,412];
    static readonly (int Count, int Length, int LongCount)[] Groups =
        [(1,16,0),(1,28,0),(1,44,0),(2,32,0),(2,43,0),(4,27,0),(4,31,0),(2,38,2),
         (3,36,2),(4,43,1),(1,50,4),(6,36,2),(8,37,1),(4,40,5),(5,41,5)];
    static readonly int[][] Centers = [[],[6,18],[6,22],[6,26],[6,30],[6,34],[6,22,38],
        [6,24,42],[6,26,46],[6,28,50],[6,30,54],[6,32,58],[6,34,62],[6,26,46,66],[6,26,48,70]];

    static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidDataException(label);
    }

    static (int Row, int Col) FormatCell(int n, int bit, bool copy)
    {
        if (copy) return bit < 8 ? (8, n-1-bit) : (n-15+bit, 8);
        return bit switch { <6 => (bit,8), <8 => (bit+1,8), 8 => (8,7), _ => (8,14-bit) };
    }

    static int ReadMask(bool[,] matrix)
    {
        int Read(bool copy)
        {
            int value = 0;
            for (int i = 14; i >= 0; i--)
            {
                var (r,c) = FormatCell(matrix.GetLength(0),i,copy);
                value = 2*value + (matrix[r,c] ? 1 : 0);
            }
            return value;
        }
        int format = Read(false), mask = Array.IndexOf(Formats,format);
        Require(mask >= 0 && format == Read(true),"格式位或纠错等级错误");
        return mask;
    }

    static IEnumerable<(int Row,int Col)> DataCells(int n)
    {
        int version = (n-17)/4;
        bool IsReserved(int r,int c)
        {
            if (r == 6 || c == 6 || (r < 9 && c < 9) || (r < 9 && c >= n-8) || (r >= n-8 && c < 9)) return true;
            if (version >= 7 && (r < 6 && c >= n-11 || c < 6 && r >= n-11)) return true;
            foreach (int y in Centers[version-1])
                foreach (int x in Centers[version-1])
                {
                    if ((x==6 && y==6) || (x==6 && y==n-7) || (x==n-7 && y==6)) continue;
                    if (Math.Abs(y-r)<=2 && Math.Abs(x-c)<=2) return true;
                }
            return false;
        }
        int row = n-1, step = -1;
        for (int col = n-1; col > 0; col -= 2)
        {
            if (col == 6) col--;
            while (row >= 0 && row < n)
            {
                for (int j = col; j >= col-1; j--) if (!IsReserved(row,j)) yield return (row,j);
                row += step;
            }
            step = -step;
            row += step;
        }
    }

    static bool Flip(int mask,int r,int c)
    {
        int p = r*c;
        int v = mask switch { 0 => (r+c)%2, 1 => r%2, 2 => c%3, 3 => (r+c)%3, 4 => (r/2+c/3)%2,
            5 => p%2+p%3, 6 => (p%2+p%3)%2, _ => ((r+c)%2+p%3)%2 };
        return v==0;
    }

    static string Decode(bool[,] matrix)
    {
        int n = matrix.GetLength(0), version = (n-17)/4, mask = ReadMask(matrix);
        var (shortCount,length,longCount) = Groups[version-1];
        var blocks = Enumerable.Range(0,shortCount+longCount).Select(i=>new byte[length+(i>=shortCount?1:0)]).ToArray();
        using var bits = DataCells(n).GetEnumerator();
        for (int i=0; i<=length; i++)
            foreach (var block in blocks)
            {
                if (i==block.Length) continue;
                for (int shift=7; shift>=0; shift--)
                {
                    Require(bits.MoveNext(),"码字提前结束");
                    var (r,c) = bits.Current;
                    if (matrix[r,c] ^ Flip(mask,r,c)) block[i] |= (byte)(1<<shift);
                }
            }
        var bytes = blocks.SelectMany(block=>block).ToArray();
        int bit=0;
        int Read(int width)
        {
            int value=0;
            for (int i=0; i<width; i++,bit++) value=value*2+((bytes[bit/8]>>(7-bit%8))&1);
            return value;
        }
        Require(Read(4)==4,"模式应为字节");
        int count=Read(version<10?8:16);
        return new UTF8Encoding(false,true).GetString(Enumerable.Range(0,count).Select(_=>(byte)Read(8)).ToArray());
    }

    static void Compare(string input, string[] rows, int? mask)
    {
        bool[,] actual = mask.HasValue ? QrCode.MaskCandidates(input).ElementAt(mask.Value) : QrCode.Encode(input);
        int n = actual.GetLength(0);
        Require(rows.Length==n && rows.All(s=>s.Length==n),"矩阵边长");
        var expected = new bool[n,n];
        for (int r=0; r<n; r++)
            for (int c=0; c<n; c++) expected[r,c]=rows[r][c]=='1';
        for (int r=0; r<n; r++)
            for (int c=0; c<n; c++)
                Require(expected[r,c]==actual[r,c],$"V{(n-17)/4} 模块 [{r},{c}] 不符");
    }

    internal static void Run()
    {
        // 前四组固定向量由公开的 ReportLab 4.4.9 qrencoder 独立生成（字节模式、M、掩码 4/0/2/2）。
        // 后两组输入同第一组：ISO/IEC 18004:2006 图 1(a) 使用掩码 5，
        // Zint 在 backend/tests/test_qr.c 发布的同一输入的矩阵使用掩码 6。
        var matrices=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"qr-matrices.txt"))
            .ReplaceLineEndings("\n").Trim().Split("\n\n");
        string[] inputs=["QR Code Symbol","hello","https://example.test/pay",string.Concat(Enumerable.Repeat("abcd",32)),"QR Code Symbol","QR Code Symbol"];
        for (int i=0; i<inputs.Length; i++) Compare(inputs[i],matrices[i].Split('\n'),i switch { 4 => 5, 5 => 6, _ => null });

        var random=new Random(114);
        void RoundTrip(string input)
        {
            var matrix=QrCode.Encode(input);
            int version=1+Array.FindIndex(Capacities,max=>max>=Encoding.UTF8.GetByteCount(input));
            Require(matrix.GetLength(0)==version*4+17 && Decode(matrix)==input,"最小版本或解码内容错误");
        }
        string[] atoms=["P","a","0","&","云","é","🙂"];
        for (int i=0; i<50; i++)
        {
            int length=random.Next(413);
            var builder=new StringBuilder();
            while (length>0)
            {
                string atom=atoms[random.Next(atoms.Length)];
                if (Encoding.UTF8.GetByteCount(atom)>length) atom="x";
                builder.Append(atom);
                length-=Encoding.UTF8.GetByteCount(atom);
            }
            RoundTrip(builder.ToString());
        }
        RoundTrip("");
        foreach (int capacity in Capacities) RoundTrip(new string('x',capacity));
        RoundTrip("https://ifdian.net/order/create?product_type=1&plan_id=0123456789abcdef0123456789abcdef&sku=abcdef0123456789abcdef0123456789&custom_order_id=P202611240000001");
        try { QrCode.Encode(new string('x',413)); }
        catch (ArgumentException) { Console.WriteLine("PASS QR：6 组矩阵、50 组随机输入、付款链接、15 个容量边界和超限异常"); return; }
        throw new InvalidDataException("版本 15 超限应抛出异常");
    }
}
