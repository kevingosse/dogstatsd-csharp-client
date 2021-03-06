using System.Text;

namespace StatsdClient
{
    internal class SerializedMetric
    {
        public StringBuilder Builder { get; } = new StringBuilder();

        public int CopyToChars(char[] charsBuffers)
        {
            var length = Builder.Length;
            if (length > charsBuffers.Length)
            {
                return -1;
            }

            Builder.CopyTo(0, charsBuffers, 0, length);
            return length;
        }

        public override string ToString()
        {
            return Builder.ToString();
        }

        public void Reset()
        {
            Builder.Clear();
        }
    }
}
