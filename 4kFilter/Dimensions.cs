namespace _4kFilter
{
    public struct Dimensions
    {
        public int width, height;

        public Dimensions(int width, int height)
        {
            this.width = width;
            this.height = height;
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
            return a.height > b.height && a.width > b.width;
        }

        public static bool operator <(Dimensions a, Dimensions b)
        {
            return a.height < b.height && a.width < b.width;
        }

        public static bool operator >=(Dimensions a, Dimensions b)
        {
            return a.height >= b.height && a.width >= b.width;
        }

        public static bool operator <=(Dimensions a, Dimensions b)
        {
            return a.height <= b.height && a.width <= b.width;
        }

        public static bool operator ==(Dimensions a, Dimensions b)
        {
            return a.height == b.height && a.width == b.width;
        }

        public static bool operator !=(Dimensions a, Dimensions b)
        {
            return a.height != b.height || a.width != b.width;
        }


        public override bool Equals(object obj)
        {
            if (!(obj is Dimensions))
            {
                return false;
            }

            var dimensions = (Dimensions)obj;
            return height == dimensions.height &&
                   width == dimensions.width;
        }

        public override int GetHashCode()
        {
            var hashCode = -2029955663;
            hashCode = hashCode * -1521134295 + base.GetHashCode();
            hashCode = hashCode * -1521134295 + height.GetHashCode();
            hashCode = hashCode * -1521134295 + width.GetHashCode();
            return hashCode;
        }
    }
}
