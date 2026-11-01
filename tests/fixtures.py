"""Shared test data. NewsReview ships without sources, so tests create their own."""


def add_source(store, category='Technology'):
    """Create an enabled source whose default category is also a managed topic."""
    if category not in store.topics():
        store.change_topic('add', category)
    identifier = store.save_source({'name': 'Example News', 'url': 'https://example.com/news/', 'feed_url': '',
                                    'category': category, 'enabled': True})
    return next(item for item in store.sources() if item['id'] == identifier)
