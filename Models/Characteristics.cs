namespace APIdIplom.Models
{
    public class Characteristics
    {
        public int CharacteristicID { get; set; }
        public string Name { get; set; }

        // Навигационное свойство для связи с ProductCharacteristics
        public ICollection<ProductCharacteristics> ProductCharacteristics { get; set; }
    }
}
