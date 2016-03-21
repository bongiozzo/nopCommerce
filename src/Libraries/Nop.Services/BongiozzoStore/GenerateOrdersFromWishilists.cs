using Nop.Core;
using Nop.Core.Data;
using Nop.Core.Domain.Common;
using Nop.Core.Domain.Customers;
using Nop.Core.Domain.Discounts;
using Nop.Core.Domain.Localization;
using Nop.Core.Domain.Messages;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Core.Domain.Shipping;
using Nop.Core.Domain.Tax;
using Nop.Services.Logging;
using Nop.Services.Messages;
using Nop.Services.Orders;
using Nop.Services.Payments;
using Nop.Services.Tasks;
using System;
using System.Collections.Generic;
using Nop.Services.Localization;
using System.Linq;
using Nop.Services.Customers;
using Nop.Services.Stores;
using Nop.Services.Events;
using System.Text;
using System.Web;
using Nop.Core.Domain.Stores;

namespace Nop.Services.BongiozzoStore
{
    public partial class GenerateOrdersFromWishlists : ITask
    {
        private readonly ILogger _logger;
        private readonly IQueuedEmailService _queuedEmailService;
        private readonly IShoppingCartService _shoppingCartService;
        private readonly IOrderService _orderService;
        private readonly IOrderProcessingService _orderProcessingService;
        private readonly CommonSettings _commonSettings;
        private readonly LocalizationSettings _localizationSettings;
        private readonly IMessageTemplateService _messageTemplateService;
        private readonly IMessageTokenProvider _messageTokenProvider;
        private readonly ITokenizer _tokenizer;
        private readonly IEmailAccountService _emailAccountService;
        private readonly EmailAccountSettings _emailAccountSettings;
        private readonly IRepository<ShoppingCartItem> _sciRepository;
        private readonly IStoreService _storeService;
        private readonly IEventPublisher _eventPublisher;
        private readonly MessageTemplatesSettings _templatesSettings;
        private readonly ILocalizationService _localizationService;

        public GenerateOrdersFromWishlists(ILogger logger,
                                           IQueuedEmailService queuedEmailService,
                                           IShoppingCartService shoppingCartService,
                                           IOrderService orderService,
                                           IOrderProcessingService orderProcessingService,
                                           CommonSettings commonSettings,
                                           LocalizationSettings localizationSettings,
                                           IMessageTemplateService messageTemplateService,
                                           IMessageTokenProvider messageTokenProvider,
                                           ITokenizer tokenizer,
                                           IEmailAccountService emailAccountService,
                                           EmailAccountSettings emailAccountSettings,
                                           IRepository<ShoppingCartItem> sciRepository,
                                           IStoreService storeService,
                                           IEventPublisher eventPublisher,
                                           MessageTemplatesSettings templateSettings,
                                           ILocalizationService localizationService
            )
        {
            this._logger = logger;
            this._queuedEmailService = queuedEmailService;
            this._shoppingCartService = shoppingCartService;
            this._orderService = orderService;
            this._orderProcessingService = orderProcessingService;
            this._commonSettings = commonSettings;
            this._localizationSettings = localizationSettings;
            this._messageTemplateService = messageTemplateService;
            this._messageTokenProvider = messageTokenProvider;
            this._tokenizer = tokenizer;
            this._emailAccountService = emailAccountService;
            this._emailAccountSettings = emailAccountSettings;
            this._sciRepository = sciRepository;
            this._storeService = storeService;
            this._eventPublisher = eventPublisher;
            this._templatesSettings = templateSettings;
            this._localizationService = localizationService;
        }

        protected virtual EmailAccount GetEmailAccountOfMessageTemplate(MessageTemplate messageTemplate, int languageId)
        {
            var emailAccounId = messageTemplate.GetLocalized(mt => mt.EmailAccountId, languageId);
            var emailAccount = _emailAccountService.GetEmailAccountById(emailAccounId);
            if (emailAccount == null)
                emailAccount = _emailAccountService.GetEmailAccountById(_emailAccountSettings.DefaultEmailAccountId);
            if (emailAccount == null)
                emailAccount = _emailAccountService.GetAllEmailAccounts().FirstOrDefault();
            return emailAccount;
        }

        protected virtual int SendNotification(MessageTemplate messageTemplate,
                EmailAccount emailAccount, int languageId, IEnumerable<Token> tokens,
                string toEmailAddress, string toName,
                string attachmentFilePath = null, string attachmentFileName = null,
                string replyToEmailAddress = null, string replyToName = null)
        {
            //retrieve localized message template data
            var bcc = messageTemplate.GetLocalized(mt => mt.BccEmailAddresses, languageId);
            var subject = messageTemplate.GetLocalized(mt => mt.Subject, languageId);
            var body = messageTemplate.GetLocalized(mt => mt.Body, languageId);

            //Replace subject and body tokens 
            var subjectReplaced = _tokenizer.Replace(subject, tokens, false);
            var bodyReplaced = _tokenizer.Replace(body, tokens, true);

            var email = new QueuedEmail
            {
                Priority = QueuedEmailPriority.High,
                From = emailAccount.Email,
                FromName = emailAccount.DisplayName,
                To = toEmailAddress,
                ToName = toName,
                ReplyTo = replyToEmailAddress,
                ReplyToName = replyToName,
                CC = string.Empty,
                Bcc = bcc,
                Subject = subjectReplaced,
                Body = bodyReplaced,
                AttachmentFilePath = attachmentFilePath,
                AttachmentFileName = attachmentFileName,
                AttachedDownloadId = messageTemplate.AttachedDownloadId,
                CreatedOnUtc = DateTime.UtcNow,
                EmailAccountId = emailAccount.Id
            };

            _queuedEmailService.InsertQueuedEmail(email);
            return email.Id;
        }

        protected virtual void AddShoppingCartTokens(IList<Token> tokens, Customer customer, Store store, int languageId)
        {
            tokens.Add(new Token("ShoppingCart.Product(s)", ProductListToHtmlTable(customer, languageId), true));
            
            ////total
            //decimal orderTotalDiscountAmountBase;
            //Discount orderTotalAppliedDiscount;
            //List<AppliedGiftCard> appliedGiftCards;
            //int redeemedRewardPoints;
            //decimal redeemedRewardPointsAmount;
            //decimal? shoppingCartTotalBase = _orderTotalCalculationService.GetShoppingCartTotal(cart,
            //    out orderTotalDiscountAmountBase, out orderTotalAppliedDiscount,
            //    out appliedGiftCards, out redeemedRewardPoints, out redeemedRewardPointsAmount);
            //if (shoppingCartTotalBase.HasValue)
            //{
            //    decimal shoppingCartTotal = _currencyService.ConvertFromPrimaryStoreCurrency(shoppingCartTotalBase.Value, _workContext.WorkingCurrency);
            //    model.OrderTotal = _priceFormatter.FormatPrice(shoppingCartTotal, true, false);
            //} 
            
            tokens.Add(new Token("ShoppingCart.CartURLForCustomer", string.Format("{0}cart", store.Url), true));
            tokens.Add(new Token("ShoppingCart.CartURLForStoreOwner", string.Format("{0}Admin/Customer/Edit/{1}", store.Url, customer.Id), true));

            //event notification - DON'T KNOW WHY!!
            _eventPublisher.EntityTokensAdded(customer, tokens);
        }

        protected virtual string ProductListToHtmlTable(Customer customer, int languageId)
        {
            var result = "";

            var sb = new StringBuilder();
            sb.AppendLine("<table border=\"0\" style=\"width:100%;\">");

            #region Products
            sb.AppendLine(string.Format("<tr style=\"background-color:{0};text-align:center;\">", _templatesSettings.Color1));
            sb.AppendLine(string.Format("<th>{0}</th>", _localizationService.GetResource("Messages.Order.Product(s).Name", languageId)));
            sb.AppendLine(string.Format("<th>{0}</th>", _localizationService.GetResource("Messages.Order.Product(s).Quantity", languageId)));
            sb.AppendLine("</tr>");

            var table = customer.ShoppingCartItems.Where(x => x.ShoppingCartType == ShoppingCartType.ShoppingCart).ToList();
            for (int i = 0; i <= table.Count - 1; i++)
            {
                var si = table[i];

                var product = si.Product;
                if (product == null)
                    continue;

                sb.AppendLine(string.Format("<tr style=\"background-color: {0};text-align: center;\">", _templatesSettings.Color2));
                //product name
                string productName = product.GetLocalized(x => x.Name, languageId);

                sb.AppendLine("<td style=\"padding: 0.6em 0.4em;text-align: left;\">" + HttpUtility.HtmlEncode(productName));
                ////attributes
                //if (!String.IsNullOrEmpty(orderItem.AttributeDescription))
                //{
                //    sb.AppendLine("<br />");
                //    sb.AppendLine(orderItem.AttributeDescription);
                //}
                ////rental info
                //if (si.Product.IsRental)
                //{
                //    var rentalStartDate = si.RentalStartDateUtc.HasValue ? si.Product.FormatRentalDate(si.RentalStartDateUtc.Value) : "";
                //    var rentalEndDate = si.RentalEndDateUtc.HasValue ? si.Product.FormatRentalDate(si.RentalEndDateUtc.Value) : "";
                //    var rentalInfo = string.Format(_localizationService.GetResource("Order.Rental.FormattedDate"),
                //        rentalStartDate, rentalEndDate);
                //    sb.AppendLine("<br />");
                //    sb.AppendLine(rentalInfo);
                //}
                ////sku
                //if (_catalogSettings.ShowProductSku)
                //{
                //    var sku = product.FormatSku(orderItem.AttributesXml, _productAttributeParser);
                //    if (!String.IsNullOrEmpty(sku))
                //    {
                //        sb.AppendLine("<br />");
                //        sb.AppendLine(string.Format(_localizationService.GetResource("Messages.Order.Product(s).SKU", languageId), HttpUtility.HtmlEncode(sku)));
                //    }
                //}
                sb.AppendLine("</td>");

                sb.AppendLine(string.Format("<td style=\"padding: 0.6em 0.4em;text-align: center;\">{0}</td>", si.Quantity));

                sb.AppendLine("</tr>");
            }
            #endregion

            sb.AppendLine("</table>");
            result = sb.ToString();
            return result;
        }

        
        public virtual void Execute()
        {
            try 
            {
                //get all wishlists with not null every X days 
                //check for every wishlist existed order

                var recurringList = from sci in _sciRepository.Table
                            where (sci.EveryXDays > 0 &&
                                   sci.ShoppingCartTypeId == (int)ShoppingCartType.Wishlist)
                            orderby sci.CustomerId, sci.Product.VendorId
                            select sci;
                var recurringItems = recurringList.ToList();
           
                foreach (var recurringItem in recurringItems)
                {
                    //already in the cart
                    var shoppingCartList = recurringItem.Customer.ShoppingCartItems.Where(
                                            x => (x.ProductId == recurringItem.ProductId &&
                                                  x.ShoppingCartType == ShoppingCartType.ShoppingCart))
                                    .LimitPerStore(recurringItem.StoreId);
                    if (shoppingCartList.Count() > 0)
                        continue;
                    
                    //check previous orders
                    var prevOrder = _orderService.SearchOrders(customerId: recurringItem.CustomerId,
                                                               productId: recurringItem.ProductId,
                                                               psIds: new List<int>() { (int)PaymentStatus.Paid } ).LastOrDefault();
                    
                    //next if previous order had happened not so long ago                
                    if (prevOrder != null &&
                        prevOrder.PaidDateUtc.HasValue &&
                        DateTime.Compare(prevOrder.PaidDateUtc.GetValueOrDefault().AddDays((int)recurringItem.EveryXDays), DateTime.Today) > 0)
                        continue;

                    _shoppingCartService.AddToCart(recurringItem.Customer, recurringItem.Product,
                            ShoppingCartType.ShoppingCart, recurringItem.StoreId,
                            null, 0,
                            null, null,
                            recurringItem.Quantity, false);

                    //notify Customer and Admin
                    var messageTemplate = _messageTemplateService.GetMessageTemplateByName("RecurringProduct.StoreOwnerNotification", recurringItem.StoreId);
                    if (messageTemplate == null || !messageTemplate.IsActive )
                        throw new NopException ("No Message Template for Recurring Product was found");

                    //email account
                    var emailAccount = GetEmailAccountOfMessageTemplate(messageTemplate, _localizationSettings.DefaultAdminLanguageId);

                    //tokens
                    var tokens = new List<Token>();
                    Store store = _storeService.GetStoreById(recurringItem.StoreId);
                    _messageTokenProvider.AddStoreTokens(tokens, store, emailAccount);
                    AddShoppingCartTokens(tokens, recurringItem.Customer, store, _localizationSettings.DefaultAdminLanguageId);
                    _messageTokenProvider.AddCustomerTokens(tokens, recurringItem.Customer);

                    //event notification
                    _eventPublisher.MessageTokensAdded(messageTemplate, tokens);

                    var toEmail = recurringItem.Customer.Email;
                    var toName = recurringItem.Customer.GetFullName();
                    //toEmail = recurringItem.Customer.BillingAddress.Email;
                    //toName = string.Format("{0} {1}", recurringItem.Customer.BillingAddress.FirstName, recurringItem.Customer.BillingAddress.LastName);

                    SendNotification(messageTemplate, emailAccount,
                        _localizationSettings.DefaultAdminLanguageId, tokens,
                        toEmail, toName);
                }

            }
            catch (Exception exc)
            {
                _logger.Error(string.Format("Error while processing Generate orders from whishlist. {0}", exc.Message), exc);
                throw;
            }
        }
    }
}
