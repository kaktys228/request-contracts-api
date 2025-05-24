namespace APIdIplom.Models
{
    public class Products
    {
        public int ProductID { get; set; }
        public string Name { get; set; }
        public int UnitID { get; set; }
        public int CategoryID { get; set; } // Добавляем ID категории

        // Навигационное свойство для связи с Units
        public Unit Unit { get; set; }

        // Навигационное свойство для связи с Category
        public Category Category { get; set; }

        // Навигационное свойство для связи с ProductCharacteristics
        public ICollection<ProductCharacteristics> ProductCharacteristics { get; set; }
    }

}
