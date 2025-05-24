using System.Reflection.PortableExecutable;

namespace APIdIplom.Models
{
    public class ProductCharacteristics
    {
        public int ProductCharacteristicID { get; set; }
        public int ProductID { get; set; }
        public int CharacteristicID { get; set; }

        // Навигационные свойства
        public Products Product { get; set; }
        public Characteristics Characteristic { get; set; }
    }
}
