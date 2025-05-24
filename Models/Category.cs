namespace APIdIplom.Models
{
    public class Category
    {
        public int CategoryID { get; set; }
        public string Name { get; set; }

        // Навигационное свойство для обратной связи с Products
        public ICollection<Products> Products { get; set; }
    }
}
