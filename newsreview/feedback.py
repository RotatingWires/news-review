"""Bounded feedback data and the compact editorial context sent to models."""
from copy import deepcopy
from datetime import datetime

DEFAULT_FEEDBACK = {'enabled': True, 'auto_update': True, 'profile': '', 'revision': 0, 'profile_revision': 0, 'ratings': {}}


def state(interests):
    return deepcopy(interests.get('feedback', DEFAULT_FEEDBACK))


def validate(value):
    if not isinstance(value, dict) or set(value) != set(DEFAULT_FEEDBACK):
        raise ValueError('Invalid feedback preferences')
    if type(value['enabled']) is not bool or type(value['auto_update']) is not bool:
        raise ValueError('Invalid feedback flags')
    if not isinstance(value['profile'], str) or len(value['profile']) > 2400:
        raise ValueError('Learned preferences must be at most 2400 characters')
    for key in ('revision', 'profile_revision'):
        if type(value[key]) is not int or not 0 <= value[key] <= 1000000000:
            raise ValueError('Invalid feedback revision')
    if value['profile_revision'] > value['revision']:
        raise ValueError('Invalid feedback profile revision')
    ratings = value['ratings']
    if not isinstance(ratings, dict) or len(ratings) > 10000:
        raise ValueError('Use at most 10000 saved article ratings')
    for url, rating in ratings.items():
        if not isinstance(url, str) or not 1 <= len(url) <= 2048 or not isinstance(rating, dict):
            raise ValueError('Invalid article feedback')
        if type(rating.get('vote')) is not int or rating['vote'] not in (-1, 1):
            raise ValueError('Choose thumbs up or down')
        for key, limit in [('reason', 320), ('headline', 500), ('source', 100), ('summary', 1200)]:
            if not isinstance(rating.get(key), str) or len(rating[key]) > limit:
                raise ValueError('Invalid feedback ' + key)
        tags = rating.get('tags')
        if not isinstance(tags, list) or len(tags) > 100 or any(not isinstance(tag, str) or len(tag) > 60 for tag in tags):
            raise ValueError('Invalid feedback tags')
        if type(rating.get('sequence')) is not int or not 0 < rating['sequence'] <= value['revision']:
            raise ValueError('Invalid feedback sequence')
        try:
            if datetime.fromisoformat(rating['updated']).tzinfo is None:
                raise ValueError('Missing timezone')
        except (KeyError, TypeError, ValueError) as exc:
            raise ValueError('Invalid feedback date') from exc
    return value


def for_model(interests):
    result = {key: interests.get(key, default) for key, default in [('guidance', ''), ('topics', []), ('exclusions', [])]}
    feedback = interests.get('feedback')
    profile = ''
    if feedback is not None and feedback['enabled']:
        profile = feedback['profile']
    elif feedback is None:
        profile = interests.get('learned_profile', '')
    if profile:
        result['learned_profile'] = profile
    return result


def examples(value):
    recent = sorted(value['ratings'].values(), key=lambda item: item['sequence'], reverse=True)[:10]
    return [{'vote': item['vote'], 'reason': item['reason'][:320], 'headline': item['headline'][:180],
             'source': item['source'][:80], 'tags': item['tags'][:4], 'summary': item['summary'][:200]} for item in recent]
