namespace APIdIplom.Models
{
    public class RequestItem
    {
        public int RequestItemID { get; set; }
        public int RequestID { get; set; }
        public int ProductID { get; set; }
        public int RequestItemCharacteristicID { get; set; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalPrice { get; set; }

        // Навигационные свойства
        public Request Request { get; set; }
        public Products Product { get; set; }
        public RequestItemCharacteristics RequestItemCharacteristic { get; set; }
    }
}
