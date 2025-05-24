namespace APIdIplom.Models
{
    public class Unit
    {
        public int UnitID { get; set; }
        public string Name { get; set; }

        // Навигационное свойство для связи с Products
        public ICollection<Products> Products { get; set; }
    }
}
