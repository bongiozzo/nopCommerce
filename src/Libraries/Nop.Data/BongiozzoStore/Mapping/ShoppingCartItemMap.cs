using Nop.Core.Domain.Orders;

namespace Nop.Data.Mapping.Orders
{
    public partial class ShoppingCartItemMap
    {
        protected override void PostInitialize()
        {
            Property(c=>c.EveryXDays).IsOptional();  // or whatever mapping you need
        }
    }
}