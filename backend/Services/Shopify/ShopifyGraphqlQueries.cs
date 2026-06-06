namespace backend.Services.Shopify;

public static class ShopifyGraphqlQueries
{
    public const string OrdersPage = """
        query OrdersPage($query: String!, $after: String) {
          orders(first: 100, after: $after, sortKey: CREATED_AT, query: $query) {
            edges {
              cursor
              node {
                id
                name
                createdAt
                cancelledAt
                displayFinancialStatus
                currentTotalPriceSet {
                  shopMoney { amount }
                }
                shippingAddress { countryCodeV2 }
                billingAddress { countryCodeV2 }
                shippingLines(first: 20) {
                  nodes {
                    title
                    originalPriceSet { shopMoney { amount } }
                    discountedPriceSet { shopMoney { amount } }
                    currentDiscountedPriceSet { shopMoney { amount } }
                  }
                }
                lineItems(first: 250) {
                  nodes {
                    quantity
                    currentQuantity
                    title
                    originalUnitPriceSet { shopMoney { amount } }
                    originalTotalSet { shopMoney { amount } }
                    discountedTotalSet { shopMoney { amount } }
                    discountAllocations {
                      allocatedAmountSet {
                        shopMoney { amount }
                      }
                    }
                    product {
                      id
                      productType
                    }
                    variant {
                      product {
                        id
                        productType
                      }
                    }
                  }
                }
              }
            }
            pageInfo { hasNextPage endCursor }
          }
        }
        """;

    public const string OrderDeliveryNodes = """
        query OrderNodes($ids:[ID!]!) {
          nodes(ids:$ids) {
            ... on Order {
              id
              shippingAddress { firstName lastName address1 address2 city zip country countryCodeV2 }
              billingAddress { firstName lastName address1 address2 city zip country countryCodeV2 }
            }
          }
        }
        """;

    public const string ProductsPage = """
        query ProductsPage($after: String) {
          products(first: 250, after: $after) {
            edges {
              cursor
              node {
                id
                legacyResourceId
                title
                productType
                vendor
                tags
                totalInventory
                authorMetafield: metafield(namespace: "custom", key: "author") {
                  value
                }
                autorMetafield: metafield(namespace: "custom", key: "autor") {
                  value
                }
                metafields(first: 25) {
                  edges {
                    node {
                      namespace
                      key
                      value
                    }
                  }
                }
                variants(first: 100) {
                  edges {
                    node {
                      id
                      title
                      inventoryQuantity
                      selectedOptions {
                        name
                        value
                      }
                    }
                  }
                }
                featuredImage {
                  url
                }
              }
            }
            pageInfo {
              hasNextPage
              endCursor
            }
          }
        }
        """;
}
