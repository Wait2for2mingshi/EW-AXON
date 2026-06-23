using System;
using System.IO;
using System.Text;

namespace EW_Assistant.Warnings
{
    /// <summary>
    /// CSV 编码探测：优先 BOM，其次校验 UTF-8，失败回退 GBK。
    /// </summary>
    public static class CsvEncoding
    {
        /// <summary>打开 CSV 读取器，自动探测编码并支持共享读。</summary>
        public static StreamReader OpenReader(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var enc = DetectEncoding(fs);
            fs.Position = 0;
            return new StreamReader(fs, enc, detectEncodingFromByteOrderMarks: false);
        }

        /// <summary>根据 BOM/UTF-8 校验/回退策略探测编码，默认返回 UTF-8。</summary>
        private static Encoding DetectEncoding(FileStream fs)
        {
            try
            {
                if (fs == null || !fs.CanRead) return new UTF8Encoding(false);

                var bom = new byte[3];
                var read = fs.Read(bom, 0, bom.Length);
                fs.Position = 0;

                if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                {
                    return new UTF8Encoding(true);
                }

                // 读取少量样本做 UTF-8 校验
                var sampleLength = (int)Math.Min(fs.Length, 4096);
                var buffer = new byte[sampleLength];
                read = fs.Read(buffer, 0, sampleLength);
                fs.Position = 0;

                if (IsUtf8(buffer, read))
                {
                    return new UTF8Encoding(false);
                }

                return Encoding.GetEncoding(936); // GBK
            }
            catch
            {
                return new UTF8Encoding(false);
            }
        }

        private static bool IsUtf8(byte[] data, int length)
        {
            int i = 0;
            while (i < length)
            {
                byte b = data[i];
                int seqLen;
                if ((b & 0x80) == 0x00)
                {
                    seqLen = 1;
                }
                else if ((b & 0xE0) == 0xC0)
                {
                    seqLen = 2;
                }
                else if ((b & 0xF0) == 0xE0)
                {
                    seqLen = 3;
                }
                else if ((b & 0xF8) == 0xF0)
                {
                    seqLen = 4;
                }
                else
                {
                    return false;
                }

                if (i + seqLen > length) return false;

                for (int j = 1; j < seqLen; j++)
                {
                    if ((data[i + j] & 0xC0) != 0x80) return false;
                }

                i += seqLen;
            }
            return true;
        }
    }
}
