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
                      id
                      title
                      barcode
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

    public const string OrderLineItemNodes = """
        query OrderLineItemNodes($ids:[ID!]!) {
          nodes(ids:$ids) {
            ... on Order {
              id
              createdAt
              cancelledAt
              displayFinancialStatus
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
                    id
                    title
                    barcode
                    product {
                      id
                      productType
                    }
                  }
                }
              }
            }
          }
        }
        """;

    public const string ProductTitleNodes = """
        query ProductTitleNodes($ids: [ID!]!) {
          nodes(ids: $ids) {
            ... on Product {
              id
              legacyResourceId
              title
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
                handle
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
                isbnMetafield: metafield(namespace: "custom", key: "isbn") {
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
                      barcode
                      price
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
