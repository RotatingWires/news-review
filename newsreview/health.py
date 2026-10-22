"""Source health describes observations, not a claim that a publisher is dead."""

from datetime import datetime, timezone


def source_health(source, now=None):
    now = now or datetime.now(timezone.utc)
    threshold = source['stale_days']
    warnings = []

    def age(value):
        if not value:
            return 0
        return max(0, (now - datetime.fromisoformat(value)).total_seconds() / 86400)

    if not source['enabled']:
        return {'status': 'disabled', 'warnings': []}
    if source['last_error']:
        warnings.append(f"Last check failed ({source['consecutive_failures']} consecutive): {source['last_error']}")
    if not source['last_checked']:
        return {'status': 'unchecked', 'warnings': ['Not checked yet. Refresh sources to test it.']}
    if age(source['last_checked']) >= threshold:
        warnings.append(f'Not checked in at least {threshold} days; source health is stale.')
    if source['newest_published'] and age(source['newest_published']) >= threshold:
        warnings.append(f'Newest reported publication is over {threshold} days old; this source may be inactive or its feed may be stale.')
    elif not source['newest_published'] and age(source['last_new_story'] or source['first_checked']) >= threshold:
        warnings.append(f'No new stories observed for at least {threshold} days. Publication dates are unavailable; activity is uncertain.')
    if source['last_count'] == 0 and not source['last_error']:
        warnings.append('The last check returned no story entries.')
    if source['collection_note']:
        warnings.append(source['collection_note'])
    status = 'broken' if source['consecutive_failures'] >= 3 else 'error' if source['last_error'] else 'warning' if warnings else 'healthy'
    return {'status': status, 'warnings': warnings}
