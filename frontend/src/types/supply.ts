/** One row in the supplies admin table (matches GET /Supply item). */
export type SupplyListItem = {
  id: string;
  supplierName: string;
  /** ISO date string (YYYY-MM-DD). */
  date: string;
  /** Backend `ProductNumber`. */
  productNumber: number;
};
