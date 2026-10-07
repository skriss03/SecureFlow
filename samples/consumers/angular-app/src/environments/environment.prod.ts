export const environment = {
  production: true,
  // Plain HTTP to the orders API, and a payment key shipped to every browser.
  apiUrl: 'http://orders-service.shop.svc.cluster.local:8080/api',
  paymentsPublishableKey: 'pk_live_51HxQbDLz3k9Vw2demoKeyNotReal',
};
