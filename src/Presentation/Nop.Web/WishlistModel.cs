using System;
using System.Collections.Generic;
using System.Web.Mvc;
using Nop.Web.Framework.Mvc;
using Nop.Web.Models.Media;

namespace Nop.Web.Models.ShoppingCart
{
    public partial class WishlistModel
    {
        public partial class ShoppingCartItemModel
        {
            public int? EveryXDays { get; set; }
        }
    }
}