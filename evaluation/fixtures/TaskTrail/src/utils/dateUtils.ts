/** Format an ISO date for display in the user's locale. */
export function formatDueDate(isoDate: string | null, locale = 'en-IN'): string {
  if (!isoDate) return 'No deadline';
  const date = new Date(isoDate);
  if (Number.isNaN(date.getTime())) return 'Invalid date';
  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium' }).format(date);
}

/** Return the number of whole days between dates, rounded down. */
export function wholeDaysBetween(start: Date, end: Date): number {
  const day = 24 * 60 * 60 * 1000;
  return Math.floor((end.getTime() - start.getTime()) / day);
}
