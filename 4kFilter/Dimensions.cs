namespace _4kFilter
{
    public struct Dimensions
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public double Ratio => (double)Width / Height;

        public Dimensions(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public static Dimensions None = new Dimensions(-1, -1);
        public static Dimensions MinDimension
        {
            get
            {
                return new Dimensions(0, 0);
            }
        }
        public static Dimensions MaxDimension
        {
            get
            {
                return new Dimensions(int.MaxValue, int.MaxValue);
            }
        }

        public static bool operator >(Dimensions a, Dimensions b)
        {
            return a.Height > b.Height && a.Width > b.Width;
        }

        public static bool operator <(Dimensions a, Dimensions b)
        {
            return a.Height < b.Height && a.Width < b.Width;
        }

        public static bool operator >=(Dimensions a, Dimensions b)
        {
            return a.Height >= b.Height && a.Width >= b.Width;
        }

        public static bool operator <=(Dimensions a, Dimensions b)
        {
            return a.Height <= b.Height && a.Width <= b.Width;
        }

        public static bool operator ==(Dimensions a, Dimensions b)
        {
            return a.Height == b.Height && a.Width == b.Width;
        }

        public static bool operator !=(Dimensions a, Dimensions b)
        {
            return a.Height != b.Height || a.Width != b.Width;
        }


        public override bool Equals(object obj)
        {
            if (!(obj is Dimensions))
            {
                return false;
            }

            var dimensions = (Dimensions)obj;
            return Height == dimensions.Height &&
                   Width == dimensions.Width;
        }

        public override int GetHashCode()
        {
            var hashCode = -2029955663;
            hashCode = hashCode * -1521134295 + base.GetHashCode();
            hashCode = hashCode * -1521134295 + Height.GetHashCode();
            hashCode = hashCode * -1521134295 + Width.GetHashCode();
            return hashCode;
        }
    }
}
